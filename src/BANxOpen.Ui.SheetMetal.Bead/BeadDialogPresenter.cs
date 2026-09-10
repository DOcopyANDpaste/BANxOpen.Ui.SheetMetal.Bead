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

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>All dialog logic, per with-block-ui.md §2 — reads dialog values via <see cref="BlockAccessor"/>,
/// hands plain values to Core, applies results via the NxAdapters services. <c>BLOCKUI_BEAD.cs</c> (hand-
/// edited per its own banner comments) delegates every callback straight into this class's public methods.
///
/// Bead creation itself is modal one-shot (logic only in <see cref="OnApply"/>, per with-block-ui.md §5);
/// everything else (<see cref="OnSelectionChanged"/>, <see cref="OnStandardChanged"/>,
/// <see cref="OnSpecChanged"/>, <see cref="OnClearAllClicked"/>) reacts live so the ShtMetal tree / SPEC
/// pickers / selection-info list track the current selection as the user works.
///
/// Material assignment goes through <see cref="SheetMetalMaterialAssignment"/>, i.e. the shared material engine,
/// so a material offered or refused here is offered or refused for the same reason in the Material Assignment
/// dialog.
///
/// A bead not created by this tool (no SPEC stamp) does not block the dialog. Its geometry is matched against
/// every Standard's SPECs: a unique match is treated as that SPEC, and anything else is shown as a warning.
/// Either way, applying a SPEC to it updates the feature to that SPEC's parameters and records the stamp.</summary>
public sealed class BeadDialogPresenter
{
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

    private IReadOnlyList<StandardInfo> _standards = Array.Empty<StandardInfo>();
    private IReadOnlyList<BeadSpecRow> _currentStandardSpecs = Array.Empty<BeadSpecRow>();

    // Recomputed on every OnSelectionChanged.
    private Body? _resolvedBody;
    private SheetMetalProfile? _profile;
    private double? _thickness;
    private string? _materialDisplayName;
    private bool _materialMissing;
    private readonly List<CurveState> _perCurve = new();

    // Only meaningful while the body has no material.
    private PickableMaterials _pickable = PickableMaterials.None;
    private bool _pickableNarrowedByStandard;

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
        BeadSettings beadSettings)
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

    /// <summary>Called from <c>initialize_cb</c>.</summary>
    public void Initialize()
    {
        try
        {
            _standards = _specCache.ListStandards();
        }
        catch (Exception ex)
        {
            _blocks.ShowError($"Could not load the Standards registry: {ex.Message}");
            _standards = Array.Empty<StandardInfo>();
        }

        _blocks.PopulateStandards(_standards.Select(s => (s.Id, s.DisplayName)).ToList());
        OnStandardChanged();
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
            _resolvedBody = null;
            _profile = null;
            _thickness = null;
            _materialMissing = false;
            _pickable = PickableMaterials.None;
            _perCurve.Clear();
            _blocks.SetSelectionInfo(Array.Empty<string>());
            RenderSheetMetalTree(bodyName: null, modeText: "Select curve(s) to begin.", errorText: null);
            return;
        }

        var bodyResult = _curveSetValidator.ResolveSingleBody(curves);
        if (!bodyResult.Ok)
        {
            ResetToError(bodyResult.Message);
            return;
        }

        _resolvedBody = bodyResult.Value;

        var profileResult = _profileReader.ReadFor(_resolvedBody!);
        if (!profileResult.Ok)
        {
            ResetToError(profileResult.Message);
            return;
        }

        var outcome = profileResult.Value!;
        _materialMissing = outcome.MaterialMissing;
        _materialDisplayName = outcome.MaterialName;
        _profile = outcome.Profile;
        _thickness = outcome.Thickness;

        if (_materialMissing)
        {
            _perCurve.Clear();
            _blocks.SetSelectionInfo(Array.Empty<string>());
            RefreshPickableMaterials();
            RenderMaterialMissingTree();
            return;
        }

        _pickable = PickableMaterials.None;
        TraceAllCurves(curves);
        UpdateModeAndSpecPickers();
    }

    private void ResetToError(string? message)
    {
        _resolvedBody = null;
        _profile = null;
        _thickness = null;
        _pickable = PickableMaterials.None;
        _perCurve.Clear();
        _blocks.SetSelectionInfo(Array.Empty<string>());
        RenderSheetMetalTree(bodyName: null, modeText: "Selection error", errorText: message);
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

        var knownSpecs = _perCurve
            .Select(s => s.KnownSpec)
            .Where(spec => spec is not null)
            .Select(spec => spec!.Value)
            .Distinct()
            .ToList();

        // Pre-fill the pickers only when every known bead agrees on one SPEC — otherwise leave the user's current
        // choice alone, since there's no single "the" existing SPEC to default to.
        if (knownSpecs.Count == 1)
        {
            var (standardId, specId) = knownSpecs[0];
            _blocks.SelectStandard(standardId);
            OnStandardChanged(); // repopulates specs (valid-only) for that standard
            EnsureSpecInPicker(specId); // a known SPEC that no longer validates must still be selectable, so its failure is visible
            _blocks.SelectSpec(specId);
        }

        // Re-validate the (possibly just pre-filled) SPEC against the current profile — a traced-back SPEC can
        // fail here if the sheet metal changed since it was created, and that must surface the same "failed
        // rule + alternatives" error OnSpecChanged shows for a user-driven pick.
        var (modeText, errorText) = CurrentModeAndErrorText();
        RenderSheetMetalTree(bodyName: _resolvedBody!.Name, modeText: modeText, errorText: errorText);
    }

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

    /// <summary>Warning for beads that could not be identified. They do not stop Apply: applying a SPEC is how
    /// they get fixed.</summary>
    private string? UnidentifiedBeadWarning()
    {
        var unmatched = _perCurve.Where(s => s.UnmatchedReason is not null).Select(s => s.Traceback.ExistingFeature?.Tag).Distinct().Count();
        return unmatched == 0
            ? null
            : $"{unmatched} selected bead(s) were not created by this tool and could not be matched to a SPEC (see the " +
              "selection list). Choose a SPEC and Apply to update them to it; the SPEC is then recorded on the bead.";
    }

    /// <summary>Renders the ShtMetal tree from current presenter state plus the given mode/error override —
    /// centralizes what would otherwise be a dozen near-duplicate <c>PopulateSheetMetalTree</c> call sites.</summary>
    private void RenderSheetMetalTree(string? bodyName, string modeText, string? errorText, string? warningText = null)
    {
        var specPreview = CurrentSpecPreview();

        _blocks.PopulateSheetMetalTree(
            bodyName,
            _profile?.Thickness ?? _thickness,
            _materialDisplayName,
            _materialMissing,
            _materialMissing ? _pickable.Materials.Select(m => m.DisplayText).ToList() : Array.Empty<string>(),
            modeText,
            specPreview?.Thickness,
            specPreview?.AllowedMaterialsSummary,
            errorText,
            OnMaterialPicked,
            warningText ?? UnidentifiedBeadWarning());

        _blocks.SetSpecPreview(specPreview?.Radius, specPreview?.Width, specPreview?.Height, specPreview?.DieRadius);
    }

    // ---- Material (only while the body has none) ----

    /// <summary>Rebuilds the materials offered for a body with no material. When a Standard is chosen, only
    /// grades one of its SPECs allows at this thickness are offered, so the user cannot pick a material that
    /// leaves no valid SPEC to apply.</summary>
    private void RefreshPickableMaterials()
    {
        if (_resolvedBody is null || !_materialMissing)
        {
            _pickable = PickableMaterials.None;
            return;
        }

        IReadOnlyCollection<string>? allowedGrades = null;
        if (_currentStandardSpecs.Count > 0 && _thickness is { } thickness)
            allowedGrades = BeadSpecFinder.AllowedGradesAt(_currentStandardSpecs, thickness);

        _pickableNarrowedByStandard = allowedGrades is not null;
        _pickable = _materialAssignment.ListPickable(BodyResolver.GetBodyId(_resolvedBody), allowedGrades);
    }

    private void RenderMaterialMissingTree()
    {
        string modeText;
        if (_pickable.Materials.Count > 0)
            modeText = "No material assigned — pick one in the Material row above.";
        else if (_pickableNarrowedByStandard)
            modeText = "No material assigned, and no library material is allowed by a SPEC in this Standard at this thickness. Choose another Standard.";
        else
            modeText = "No material assigned, and no library material can be assigned to this body.";

        var warnings = _pickable.Warnings.Count > 0 ? string.Join(" ", _pickable.Warnings) : null;
        RenderSheetMetalTree(bodyName: _resolvedBody?.Name, modeText: modeText, errorText: null, warningText: warnings);
    }

    private void OnMaterialPicked(string displayText)
    {
        if (_resolvedBody is null)
            return;

        var picked = _pickable.Materials.FirstOrDefault(m => m.DisplayText == displayText);
        if (picked is null)
            return;

        var result = _materialAssignment.Assign(BodyResolver.GetBodyId(_resolvedBody), picked.Material, _blocks.Confirm);
        if (!result.Ok)
        {
            // Declining the confirmation is the user's own choice, not an error to report back to them.
            if (result.ErrorCode != "ASSIGNMENT_DECLINED")
                _blocks.ShowError(result.Message ?? "Material assignment failed.");
            return;
        }

        if (result.Value is { Count: > 0 } warnings)
        {
            _blocks.ShowResult(
                OperationResult.Success(),
                $"'{picked.Material.Name}' assigned.{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, warnings)}");
        }

        // Material is now on the body — re-derive everything (thickness/material read, traceback, pickers).
        OnSelectionChanged();
    }

    // ---- Standard / SPEC ----

    public void OnStandardChanged()
    {
        var standardId = _blocks.GetSelectedStandardId();
        var standard = _standards.FirstOrDefault(s => s.Id == standardId);
        if (standard is null)
        {
            _currentStandardSpecs = Array.Empty<BeadSpecRow>();
            _blocks.PopulateSpecs(Array.Empty<string>());
        }
        else
        {
            try
            {
                _currentStandardSpecs = _specCache.GetSpecs(standard);
            }
            catch (Exception ex)
            {
                _blocks.ShowError($"Could not load specs for Standard '{standard.DisplayName}': {ex.Message}");
                _currentStandardSpecs = Array.Empty<BeadSpecRow>();
            }

            RepopulateSpecPicker();
        }

        // Which materials may be offered depends on the Standard, so a body still waiting for one is refreshed.
        if (_materialMissing && _resolvedBody is not null)
        {
            RefreshPickableMaterials();
            RenderMaterialMissingTree();
        }
    }

    private void RepopulateSpecPicker()
    {
        var specIds = _profile is not null
            ? _specFinder.FindValid(_profile, _currentStandardSpecs).Select(s => s.SpecId).ToList()
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

        var specIds = _profile is not null
            ? _specFinder.FindValid(_profile, _currentStandardSpecs).Select(s => s.SpecId).ToList()
            : _currentStandardSpecs.Select(s => s.SpecId).ToList();

        if (!specIds.Contains(specId))
            specIds.Add(specId);

        _blocks.PopulateSpecs(specIds);
    }

    public void OnSpecChanged()
    {
        if (_profile is null)
            return;

        var (modeText, errorText) = CurrentModeAndErrorText();
        RenderSheetMetalTree(_resolvedBody?.Name, modeText, errorText);
    }

    private (string ModeText, string? ErrorText) CurrentModeAndErrorText()
    {
        if (_profile is null)
            return ("", null);

        var spec = SelectedSpec();
        if (spec is null)
            return ("Choose a SPEC.", null);

        var result = _validator.Validate(_profile, spec);
        if (result.IsValid)
            return (CurrentModeText(), null);

        var alternatives = _specFinder.FindValid(_profile, _currentStandardSpecs).Select(s => s.SpecId).ToList();
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

    private BeadSpecRow? SelectedSpec()
    {
        var specId = _blocks.GetSelectedSpecId();
        return _currentStandardSpecs.FirstOrDefault(s => s.SpecId == specId);
    }

    private SpecPreview? CurrentSpecPreview()
    {
        var spec = SelectedSpec();
        if (spec is null)
            return null;

        var allowedSummary = string.Join(", ", spec.AllowedMaterialGrades.Where(kv => kv.Value).Select(kv => kv.Key));
        return new SpecPreview(spec.RadiusAndRadS, spec.Width, spec.Height, spec.DieRadiusP, spec.Thickness, allowedSummary);
    }

    private sealed record SpecPreview(double Radius, double Width, double Height, double DieRadius, double Thickness, string AllowedMaterialsSummary);

    // ---- Apply ----

    public int OnApply()
    {
        if (_profile is null)
        {
            _blocks.ShowError("Select curve(s) on a sheet metal body first.");
            return 1;
        }

        var spec = SelectedSpec();
        if (spec is null)
        {
            _blocks.ShowError("Choose a SPEC first.");
            return 1;
        }

        var validation = _validator.Validate(_profile, spec);
        if (!validation.IsValid)
        {
            _blocks.ShowError(validation.BlockingMessage ?? "SPEC does not validate against this sheet metal.");
            return 1;
        }

        using var undo = new UndoScope(_context.Session, "Create/Update Bead", _context.Log.Error);
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
        _blocks.ShowResult(OperationResult.Success(), "Bead(s) created/updated.");
        OnSelectionChanged();
        return 0;
    }
}
