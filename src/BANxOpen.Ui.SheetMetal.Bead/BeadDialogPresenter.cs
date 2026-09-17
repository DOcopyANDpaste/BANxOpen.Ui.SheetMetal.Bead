using BANxOpen.SheetMetal.Beads;
using BANxOpen.SheetMetal.SpecData;
using NXOpen;
using NXOpen.Features;
using BANxOpen.SheetMetal.NxAdapters.Beads;
using BANxOpen.SheetMetal.NxAdapters.Common;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.Foundation.NxAdapters.Materials;
using BANxOpen.SheetMetal.Common;
using BANxOpen.SheetMetal.Materials;

// NXOpen has its own material types; this is the shared material library's.
using LibraryMaterial = BANxOpen.Foundation.Contracts.Materials.Material;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>All dialog logic, per with-block-ui.md §2 — reads dialog values via <see cref="BlockAccessor"/>,
/// hands plain values to Core, applies results via the NxAdapters services. <c>BLOCKUI_BEAD.cs</c> (hand-
/// edited per its own banner comments) delegates every callback straight into this class's public methods.
///
/// The user first picks the sheet metal material: a Standard, then one of its rows in NX's sheet metal material
/// standards file. The row decides everything a SPEC is judged by — the grade, the thickness, and (through its
/// Standard's folder) which bead SPECs exist at all. The Standard is chosen once for the part: it is the Standard of
/// the row the part's Sheet Metal Preferences are set to, and is preselected from them.
/// TODO(business): confirm the Standard is part-level rather than chosen per bead.
///
/// Picking changes nothing in NX. On Apply, in one undo mark, the body is given the row's physical material (through
/// <see cref="SheetMetalMaterialAssignment"/>, i.e. the shared material engine, asking first when that replaces a
/// different material), the part's Sheet Metal Preferences are set to the row, and the beads are built. Any failure
/// or refusal undoes all of it.
///
/// Bead creation itself is modal one-shot (logic only in <see cref="OnApply"/>, per with-block-ui.md §5);
/// everything else reacts live so the ShtMetal tree / pickers / selection-info list track the current selection.
///
/// A bead not created by this tool (no SPEC stamp) does not block the dialog. Its geometry is matched against
/// every Standard's SPECs: a unique match is treated as that SPEC, and anything else is shown as a warning.
/// Either way, applying a SPEC to it updates the feature to that SPEC's parameters and records the stamp.</summary>
public sealed class BeadDialogPresenter
{
    /// <summary>The sheet metal material picker's first member, so a row is only ever one the user chose.</summary>
    private const string ChooseMaterialOption = "(choose a sheet metal material)";

    private readonly NxSessionContext _context;
    private readonly BlockAccessor _blocks;
    private readonly BeadSpecCache _specCache;
    private readonly BeadSpecValidator _validator;
    private readonly BeadSpecFinder _specFinder;
    private readonly SelectedCurveSetValidator _curveSetValidator;
    private readonly SheetMetalProfileReader _profileReader;
    private readonly BeadTracebackService _tracebackService;
    private readonly BeadFeatureService _featureService;
    private readonly SheetMetalMaterialAssignment _materialAssignment;
    private readonly IBeadSpecLookup _specLookup;
    private readonly BeadGeometryReader _geometryReader;
    private readonly BeadSettings _beadSettings;
    private readonly SheetMetalPreferenceService _preferences;
    private readonly SheetMetalMaterialTable _materialTable;

    private IReadOnlyList<StandardInfo> _standards = Array.Empty<StandardInfo>();

    // The chosen Standard, its SPECs, and why it has none when it has none.
    private StandardInfo? _standard;
    private IReadOnlyList<BeadSpecRow> _currentStandardSpecs = Array.Empty<BeadSpecRow>();
    private string? _noSpecsReason;

    // The chosen sheet metal material. Survives selection changes: it is the user's choice, not a fact of the body.
    private SheetMetalMaterialRow? _pickedRow;
    private IReadOnlyDictionary<string, SheetMetalMaterialRow> _rowsByOption = new Dictionary<string, SheetMetalMaterialRow>();

    // The body the preferences' row was last preselected for, so re-selecting curves on it keeps the user's own pick.
    private BodyId? _preselectedFor;

    // Recomputed on every OnSelectionChanged.
    private Body? _resolvedBody;
    private ProfileReadOutcome? _outcome;
    private readonly List<CurveState> _perCurve = new();

    public BeadDialogPresenter(
        NxSessionContext context,
        BlockAccessor blocks,
        BeadSpecCache specCache,
        BeadSpecValidator validator,
        BeadSpecFinder specFinder,
        SelectedCurveSetValidator curveSetValidator,
        SheetMetalProfileReader profileReader,
        BeadTracebackService tracebackService,
        BeadFeatureService featureService,
        SheetMetalMaterialAssignment materialAssignment,
        IBeadSpecLookup specLookup,
        BeadGeometryReader geometryReader,
        BeadSettings beadSettings,
        SheetMetalPreferenceService preferences,
        SheetMetalMaterialTable materialTable)
    {
        _context = context;
        _blocks = blocks;
        _specCache = specCache;
        _validator = validator;
        _specFinder = specFinder;
        _curveSetValidator = curveSetValidator;
        _profileReader = profileReader;
        _tracebackService = tracebackService;
        _featureService = featureService;
        _materialAssignment = materialAssignment;
        _specLookup = specLookup;
        _geometryReader = geometryReader;
        _beadSettings = beadSettings;
        _preferences = preferences;
        _materialTable = materialTable;
    }

    /// <summary>One selected curve, what it traces back to, and — for a bead with no stamp — what its geometry
    /// was matched to.</summary>
    /// <param name="MatchedSpec">The SPEC an unstamped bead was identified as, or null.</param>
    /// <param name="UnmatchedReason">Why an unstamped bead could not be identified, or null.</param>
    private sealed record CurveState(NXObject Curve, CurveTraceback Traceback, BeadSpecRow? MatchedSpec, string? UnmatchedReason)
    {
        public bool IsUnstamped => Traceback.Result.HasUnstampedFeature;

        public bool IsExistingFeature => Traceback.ExistingFeature is not null;

        /// <summary>The Standard/SPEC this curve's bead is known to be, from its stamp or its geometry.</summary>
        public (string StandardId, string SpecId)? KnownSpec =>
            Traceback.Result is { Found: true, StandardId: { } standardId, SpecId: { } specId }
                ? (standardId, specId)
                : MatchedSpec is { } matched ? (matched.StandardId, matched.SpecId) : null;
    }

    /// <summary>The facts a SPEC is validated against: the body, made to the chosen sheet metal material. Null until
    /// both are known.</summary>
    private SheetMetalProfile? Profile => _outcome is { } outcome && _pickedRow is { } row ? outcome.ProfileFor(row) : null;

    /// <summary>Called from <c>initialize_cb</c>.</summary>
    public void Initialize()
    {
        try
        {
            _standards = _specCache.ListStandards();
        }
        catch (Exception ex)
        {
            _blocks.ShowError($"Could not list the Standards in the sheet metal material standards file: {ex.Message}");
            _standards = Array.Empty<StandardInfo>();
        }

        _blocks.PopulateStandards(_standards.Select(s => (s.Id, s.DisplayName)).ToList());
        LoadSelectedStandard();
        OnSelectionChanged();
    }

    // ---- Selection ----

    public void OnClearAllClicked()
    {
        _blocks.ClearSelection();
        OnSelectionChanged();
    }

    public void OnSelectionChanged()
    {
        var curves = _blocks.GetSelectedCurves();

        if (curves.Count == 0)
        {
            ClearBody();
            RefreshMaterialPicker();
            Render("Select curve(s) on a sheet metal body to begin.", errorText: null);
            return;
        }

        var bodyResult = _curveSetValidator.ResolveSingleBody(curves);
        if (!bodyResult.Ok)
        {
            ResetToError(bodyResult.Message);
            return;
        }

        var profileResult = _profileReader.ReadFor(bodyResult.Value!);
        if (!profileResult.Ok)
        {
            ResetToError(profileResult.Message);
            return;
        }

        _resolvedBody = bodyResult.Value;
        _outcome = profileResult.Value!;

        PreselectFromPreferences(_outcome);
        RefreshMaterialPicker();
        RepopulateSpecPicker();
        TraceAllCurves(curves);
        UpdateModeAndSpecPickers();
    }

    private void ClearBody()
    {
        _resolvedBody = null;
        _outcome = null;
        _perCurve.Clear();
        _blocks.SetSelectionInfo(Array.Empty<string>());
    }

    private void ResetToError(string? message)
    {
        ClearBody();
        RefreshMaterialPicker();
        Render("Selection error", message);
    }

    /// <summary>The first time a body is selected, its part's Sheet Metal Preferences say which Standard and row it is
    /// already made to. Later selections on the same body leave the user's own pick alone.</summary>
    private void PreselectFromPreferences(ProfileReadOutcome outcome)
    {
        if (_preselectedFor == outcome.BodyId)
            return;

        _preselectedFor = outcome.BodyId;

        if (!outcome.Preference.IsMaterialTableEntry || outcome.Preference.Row is not { } row)
            return;

        if (_standard is null || !string.Equals(_standard.Id, row.Standard, StringComparison.OrdinalIgnoreCase))
        {
            _blocks.SelectStandard(row.Standard);
            LoadSelectedStandard();
        }

        _pickedRow = row;
    }

    private void TraceAllCurves(IReadOnlyList<NXObject> curves)
    {
        _perCurve.Clear();

        // An unstamped bead is searched for across every Standard, and several selected curves can belong to
        // the same bead, so the spec list and each bead's identification are computed once per selection.
        IReadOnlyList<BeadSpecRow>? allSpecs = null;
        var identifiedByFeature = new Dictionary<Tag, (BeadSpecRow? Matched, string? Reason)>();

        foreach (var curve in curves)
        {
            var traceback = _tracebackService.Trace(curve);
            BeadSpecRow? matched = null;
            string? unmatchedReason = null;

            if (traceback.Result.HasUnstampedFeature && traceback.ExistingFeature is { } feature)
            {
                if (!identifiedByFeature.TryGetValue(feature.Tag, out var identified))
                {
                    identified = Identify(feature, ref allSpecs);
                    identifiedByFeature[feature.Tag] = identified;
                }

                (matched, unmatchedReason) = identified;
            }

            _perCurve.Add(new CurveState(curve, traceback, matched, unmatchedReason));
        }
    }

    private (BeadSpecRow? Matched, string? Reason) Identify(Feature feature, ref IReadOnlyList<BeadSpecRow>? allSpecs)
    {
        var geometry = _geometryReader.Read(feature, _resolvedBody!);
        if (geometry is null)
            return (null, "its geometry could not be read");

        allSpecs ??= _specLookup.AllSpecs();
        var match = BeadSpecMatcher.Match(geometry, allSpecs, _beadSettings);

        if (match.Single is { } row)
            return (row, null);

        return match.IsAmbiguous
            ? (null, $"its geometry matches several SPECs ({string.Join(", ", match.Candidates.Select(c => c.SpecId))})")
            : (null, "its geometry matches no SPEC in any Standard");
    }

    private void UpdateModeAndSpecPickers()
    {
        UpdateSelectionInfoList();

        // Pre-fill the SPEC only when every known bead agrees on one SPEC in the chosen Standard — otherwise leave the
        // user's current choice alone. The Standard itself is never switched: it belongs to the sheet metal material.
        var knownSpecs = KnownSpecs();
        if (knownSpecs.Count == 1 && IsCurrentStandard(knownSpecs[0].StandardId))
        {
            var specId = knownSpecs[0].SpecId;
            EnsureSpecInPicker(specId); // a known SPEC that no longer validates must still be selectable, so its failure is visible
            _blocks.SelectSpec(specId);
        }

        // Re-validate the (possibly just pre-filled) SPEC — a traced-back SPEC can fail if the sheet metal changed
        // since it was created, and that must surface the same "failed rule + alternatives" error a user-driven pick
        // shows.
        Render();
    }

    private List<(string StandardId, string SpecId)> KnownSpecs() =>
        _perCurve
            .Select(s => s.KnownSpec)
            .Where(spec => spec is not null)
            .Select(spec => spec!.Value)
            .Distinct()
            .ToList();

    private bool IsCurrentStandard(string standardId) =>
        _standard is not null && string.Equals(_standard.Id, standardId, StringComparison.OrdinalIgnoreCase);

    private void UpdateSelectionInfoList()
    {
        var lines = new List<string>();
        for (var i = 0; i < _perCurve.Count; i++)
        {
            var state = _perCurve[i];
            var label = string.IsNullOrEmpty(state.Curve.Name) ? $"Curve{i + 1}" : state.Curve.Name;
            var result = state.Traceback.Result;

            var status = state switch
            {
                { IsUnstamped: true, MatchedSpec: { } matched } =>
                    $"Not created by this tool; geometry matches SPEC {matched.SpecId} — Apply records it",
                { IsUnstamped: true } =>
                    $"WARNING: not created by this tool, {state.UnmatchedReason} — choose a SPEC and Apply to update it",
                _ when result is { Found: true, IsPatternInstance: true } => $"Editing SPEC {result.SpecId} (via pattern)",
                _ when result.Found => $"Editing SPEC {result.SpecId}",
                _ => "New bead",
            };

            lines.Add($"{label} — {status}");
        }

        _blocks.SetSelectionInfo(lines);
    }

    // ---- Sheet metal material ----

    /// <summary>Called from <c>update_cb</c> for the Standard picker.</summary>
    public void OnStandardChanged()
    {
        LoadSelectedStandard();
        RefreshMaterialPicker();
        RepopulateSpecPicker();
        Render();
    }

    /// <summary>Called from <c>update_cb</c> for the sheet metal material picker.</summary>
    public void OnSheetMetalMaterialChanged()
    {
        _pickedRow = _blocks.GetSelectedSheetMetalMaterial() is { } option && _rowsByOption.TryGetValue(option, out var row)
            ? row
            : null;

        RepopulateSpecPicker();
        Render();
    }

    /// <summary>Reads the chosen Standard and its bead SPECs. A picked row from another Standard is dropped: the
    /// material and the Standard must agree.</summary>
    private void LoadSelectedStandard()
    {
        var standardId = _blocks.GetSelectedStandardId();
        _standard = _standards.FirstOrDefault(s => s.Id == standardId);
        _currentStandardSpecs = Array.Empty<BeadSpecRow>();
        _noSpecsReason = null;

        if (_pickedRow is not null && !IsCurrentStandard(_pickedRow.Standard))
            _pickedRow = null;

        if (_standard is null)
            return;

        try
        {
            _currentStandardSpecs = _specCache.GetSpecs(_standard);
            if (_currentStandardSpecs.Count == 0)
            {
                _noSpecsReason = _specCache.FindWorkbook(_standard) is null
                    ? $"there is no bead SPEC workbook (.xlsx) in '{_standard.BeadSpecFolder}'"
                    : "its bead SPEC workbook lists no SPECs";
            }
        }
        catch (Exception ex)
        {
            _noSpecsReason = $"its bead SPECs could not be read: {ex.Message}";
        }
    }

    /// <summary>Lists the chosen Standard's rows. Every row is offered; one whose thickness differs from the body is
    /// marked, and picking it is reported rather than hidden.</summary>
    private void RefreshMaterialPicker()
    {
        var rows = _standard is null ? Array.Empty<SheetMetalMaterialRow>() : _materialTable.RowsFor(_standard.Id);

        var rowsByOption = new Dictionary<string, SheetMetalMaterialRow>();
        foreach (var row in rows)
            rowsByOption[Describe(row)] = row;

        _rowsByOption = rowsByOption;
        _blocks.PopulateSheetMetalMaterials(new[] { ChooseMaterialOption }.Concat(rowsByOption.Keys).ToList());

        var picked = _pickedRow is null ? null : rowsByOption.FirstOrDefault(kv => kv.Value.Name == _pickedRow.Name).Key;
        _blocks.SelectSheetMetalMaterial(picked ?? ChooseMaterialOption);
    }

    private string Describe(SheetMetalMaterialRow row)
    {
        var text = $"{row.Name}   (t {row.Thickness:0.####}, R {row.BendRadius})";
        return ThicknessDiffers(row) ? $"{text}   ** thickness differs from body **" : text;
    }

    private bool ThicknessDiffers(SheetMetalMaterialRow row) =>
        _outcome is { } outcome && !SheetMetalPreferenceCheck.ThicknessMatches(row.Thickness, outcome.Thickness);

    private bool PhysicalMaterialDiffers(SheetMetalMaterialRow row) =>
        _outcome is { PhysicalMaterialName: { } bodyMaterial }
        && !string.Equals(bodyMaterial, row.PhysicalMaterialName, StringComparison.OrdinalIgnoreCase);

    // ---- SPEC ----

    private void RepopulateSpecPicker()
    {
        var specIds = Profile is { } profile
            ? _specFinder.FindValid(profile, _currentStandardSpecs).Select(s => s.SpecId).ToList()
            : _currentStandardSpecs.Select(s => s.SpecId).ToList();

        _blocks.PopulateSpecs(specIds);
    }

    /// <summary>Adds <paramref name="specId"/> to the picker if it's a real row in the current Standard but
    /// didn't make the valid-only list <see cref="RepopulateSpecPicker"/> builds — otherwise a known SPEC
    /// that's stopped validating (sheet metal changed since it was created) would have nothing to select, and
    /// its failure would have no way to become visible in the dialog.</summary>
    private void EnsureSpecInPicker(string specId)
    {
        if (_currentStandardSpecs.All(s => s.SpecId != specId))
            return;

        var specIds = Profile is { } profile
            ? _specFinder.FindValid(profile, _currentStandardSpecs).Select(s => s.SpecId).ToList()
            : _currentStandardSpecs.Select(s => s.SpecId).ToList();

        if (!specIds.Contains(specId))
            specIds.Add(specId);

        _blocks.PopulateSpecs(specIds);
    }

    public void OnSpecChanged() => Render();

    private BeadSpecRow? SelectedSpec()
    {
        var specId = _blocks.GetSelectedSpecId();
        return _currentStandardSpecs.FirstOrDefault(s => s.SpecId == specId);
    }

    // ---- State and rendering ----

    /// <summary>What the dialog is doing, and the one thing stopping Apply, if any — in the order the user has to
    /// resolve them.</summary>
    private (string ModeText, string? ErrorText) CurrentModeAndErrorText()
    {
        if (_outcome is not { } outcome)
            return ("Select curve(s) on a sheet metal body to begin.", null);

        if (_standard is null)
            return ("Choose a Standard.", null);

        if (_pickedRow is not { } row)
            return ("Choose a sheet metal material.", null);

        if (ThicknessDiffers(row))
        {
            return (CurrentModeText(),
                $"Sheet metal material '{row.Name}' is {row.Thickness:0.####} thick, but this sheet metal is " +
                $"{outcome.Thickness:0.####}. Choose a material of this thickness, or correct the body's thickness.");
        }

        if (_noSpecsReason is not null)
            return (CurrentModeText(), $"No bead SPEC is allowed under Standard '{_standard.Id}': {_noSpecsReason}.");

        var spec = SelectedSpec();
        if (spec is null)
            return ("Choose a SPEC.", null);

        var profile = outcome.ProfileFor(row);
        var result = _validator.Validate(profile, spec);
        if (result.IsValid)
            return (CurrentModeText(), null);

        var alternatives = _specFinder.FindValid(profile, _currentStandardSpecs).Select(s => s.SpecId).ToList();
        var alternativesText = alternatives.Count > 0
            ? $" Valid alternatives in this Standard: {string.Join(", ", alternatives)}."
            : " No SPECs in this Standard currently validate against this sheet metal.";

        return (CurrentModeText(), $"{result.BlockingMessage}{alternativesText}");
    }

    private string CurrentModeText()
    {
        var updateCount = _perCurve.Count(s => s.IsExistingFeature);
        var createCount = _perCurve.Count - updateCount;
        return updateCount switch
        {
            0 => $"New bead(s): {createCount} curve(s) selected.",
            _ when createCount == 0 => $"Editing existing bead(s): {updateCount} curve(s).",
            _ => $"Mixed: {createCount} new, {updateCount} existing bead(s) to update.",
        };
    }

    private void Render()
    {
        var (modeText, errorText) = CurrentModeAndErrorText();
        Render(modeText, errorText);
    }

    /// <summary>Renders the ShtMetal tree from current presenter state plus the given mode/error — centralizes what
    /// would otherwise be a dozen near-duplicate <c>PopulateSheetMetalTree</c> call sites.</summary>
    private void Render(string modeText, string? errorText)
    {
        var spec = SelectedSpec();

        _blocks.PopulateSheetMetalTree(
            _resolvedBody?.Name,
            _outcome?.Thickness,
            _outcome?.PhysicalMaterialName,
            SheetMetalMaterialText(),
            PreferencesText(),
            modeText,
            spec?.Thickness,
            spec is null ? null : string.Join(", ", spec.AllowedMaterialGrades.Where(kv => kv.Value).Select(kv => kv.Key)),
            errorText,
            Warnings());

        _blocks.SetSpecPreview(spec?.RadiusAndRadS, spec?.Width, spec?.Height, spec?.DieRadiusP);
    }

    private string? SheetMetalMaterialText()
    {
        if (_pickedRow is not { } row)
            return _standard is null ? "(choose a Standard)" : "(not chosen)";

        var text = $"{row.Name} — {row.PhysicalMaterialName}, grade {row.Grade}";
        if (_outcome is { PhysicalMaterialName: null })
            text += " (assigned to the body on Apply)";
        else if (PhysicalMaterialDiffers(row))
            text += $" (replaces '{_outcome!.PhysicalMaterialName}' on Apply, after confirmation)";

        return text;
    }

    private string? PreferencesText()
    {
        if (_outcome is not { Preference: var preference })
            return null;

        var current = preference switch
        {
            { IsMaterialTableEntry: false } => "not Material Table entry",
            { MaterialName: null } => "no material",
            { Row: null } => $"'{preference.MaterialName}' (not in the standards file)",
            _ => $"'{preference.MaterialName}'",
        };

        return _pickedRow is { } row && !preference.UsesMaterial(row.Name)
            ? $"{current} — set to '{row.Name}' on Apply"
            : current;
    }

    /// <summary>Advisories that do not stop Apply.</summary>
    private string? Warnings()
    {
        var warnings = new List<string>();

        var unmatched = _perCurve.Where(s => s.UnmatchedReason is not null).Select(s => s.Traceback.ExistingFeature?.Tag).Distinct().Count();
        if (unmatched > 0)
        {
            warnings.Add(
                $"{unmatched} selected bead(s) were not created by this tool and could not be matched to a SPEC (see the " +
                "selection list). Choose a SPEC and Apply to update them to it; the SPEC is then recorded on the bead.");
        }

        var otherStandards = KnownSpecs().Select(k => k.StandardId).Where(id => !IsCurrentStandard(id)).Distinct().ToList();
        if (otherStandards.Count > 0 && _standard is not null)
        {
            warnings.Add(
                $"Selected bead(s) were built to Standard {string.Join(", ", otherStandards)}, not '{_standard.Id}'. " +
                "Apply updates them to the chosen SPEC in this Standard.");
        }

        if (_outcome is { Preference: { SheetMetalBodyCount: > 1 } preference }
            && _pickedRow is { } row && !preference.UsesMaterial(row.Name))
        {
            warnings.Add(
                $"This part has {preference.SheetMetalBodyCount} sheet metal bodies. Sheet Metal Preferences belong to the " +
                "part, so Apply sets them for all of them.");
        }

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    // ---- Apply ----

    public int OnApply()
    {
        if (_outcome is not { } outcome || _resolvedBody is null)
        {
            _blocks.ShowError("Select curve(s) on a sheet metal body first.");
            return 1;
        }

        if (_pickedRow is not { } row)
        {
            _blocks.ShowError("Choose a Standard and a sheet metal material first.");
            return 1;
        }

        var (_, blocking) = CurrentModeAndErrorText();
        if (blocking is not null)
        {
            _blocks.ShowError(blocking);
            return 1;
        }

        var spec = SelectedSpec();
        if (spec is null)
        {
            _blocks.ShowError("Choose a SPEC first.");
            return 1;
        }

        // Asked before anything changes, so declining leaves the part exactly as it was.
        if (PhysicalMaterialDiffers(row) && !_blocks.Confirm(
                $"This body's material is '{outcome.PhysicalMaterialName}', but sheet metal material '{row.Name}' is made of " +
                $"'{row.PhysicalMaterialName}'.{Environment.NewLine}{Environment.NewLine}" +
                $"Replace the body's material with '{row.PhysicalMaterialName}'?"))
        {
            return 1;
        }

        var needsMaterial = outcome.PhysicalMaterialName is null || PhysicalMaterialDiffers(row);
        LibraryMaterial? material = null;
        if (needsMaterial)
        {
            var found = _materialAssignment.FindMaterial(row.PhysicalMaterialName);
            if (!found.Ok)
            {
                _blocks.ShowError(found.Message ?? $"Physical material '{row.PhysicalMaterialName}' could not be found.");
                return 1;
            }

            material = found.Value;
        }

        // Everything below shares one undo mark: returning without Commit undoes the material, the preferences and any
        // bead already built.
        using var undo = new UndoScope(_context.Session, "Create/Update Bead", _context.Log.Error);
        var warnings = new List<string>();

        if (material is not null)
        {
            var assigned = _materialAssignment.Assign(BodyResolver.GetBodyId(_resolvedBody), material, _blocks.Confirm);
            if (!assigned.Ok)
            {
                // Declining the engine's own confirmation is the user's choice, not an error to report back to them.
                if (assigned.ErrorCode != "ASSIGNMENT_DECLINED")
                    _blocks.ShowError(assigned.Message ?? "Material assignment failed. No changes were made.");
                return 1;
            }

            warnings.AddRange(assigned.Value ?? Array.Empty<string>());
        }

        // Assigning a material also sets the preferences to that material's first row, so they are set to the picked
        // row whenever a material was assigned, not only when they differed beforehand.
        if (material is not null || !outcome.Preference.UsesMaterial(row.Name))
        {
            var synced = _preferences.SyncMaterial(row);
            if (!synced.Ok)
            {
                _blocks.ShowError($"{synced.Message ?? "Sheet Metal Preferences could not be updated."} No changes were made.");
                return 1;
            }
        }

        var anyFailed = false;

        foreach (var state in _perCurve)
        {
            var existing = state.Traceback.ExistingFeature;
            var isUpdate = existing is not null;

            // An existing bead — stamped, or built by hand — is updated in place to the validated SPEC's
            // parameters. For a hand-built one this is also what records its SPEC for the first time.
            var result = _featureService.CreateOrUpdate(state.Curve, spec, existing);
            if (!result.Ok)
            {
                anyFailed = true;
                _context.Log.Error($"Bead {(isUpdate ? "update" : "create")} failed for curve '{state.Curve.Name}': {result.Message}");
                continue;
            }

            BeadAttributeWriter.Stamp(result.Value!, spec.StandardId, spec.SpecId, isNewFeature: !isUpdate);
        }

        if (anyFailed)
        {
            _blocks.ShowError("One or more beads failed — see the listing window for details. No changes were committed.");
            return 1;
        }

        undo.Commit();

        var message = "Bead(s) created/updated.";
        if (warnings.Count > 0)
            message += $"{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, warnings)}";

        _blocks.ShowResult(OperationResult.Success(), message);
        OnSelectionChanged();
        return 0;
    }
}
