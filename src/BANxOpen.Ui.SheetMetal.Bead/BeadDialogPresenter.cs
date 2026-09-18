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
/// Mode, per with-block-ui.md §5: INTERACTIVE for the pickers — the tree, the filters and the SPEC list all
/// react live so they track the current selection — and MODAL ONE-SHOT for the model, which is only touched
/// in <see cref="OnApply"/>.
///
/// The user picks a sheet metal material in three steps: a Standard, then a physical material within it, then
/// one of that material's rows in the tree — the row carries the grade, the thickness and the bend radius, and
/// only a row is specific enough to judge a SPEC by. Exactly one row is ever checked. The Standard is chosen
/// once for the part: it is the Standard of the row the part's Sheet Metal Preferences are set to, and is
/// preselected from them.
/// TODO(business): confirm the Standard is part-level rather than chosen per bead.
///
/// The bead SPEC is chosen separately: the <c>enum_BABead</c> radio names one of the Standard's bead SPEC
/// workbooks, and that workbook's rows — filtered to the ones that validate against the chosen material — fill
/// the variants list. The radio's members come from the .dlx, not from code.
///
/// Picking changes nothing in NX. On Apply, in one undo mark, the body is given the row's physical material
/// (through <see cref="SheetMetalMaterialAssignment"/>, i.e. the shared material engine, asking first when that
/// replaces a different material), the part's Sheet Metal Preferences are set to the row, and the beads are
/// built. Any failure or refusal undoes all of it.
///
/// A bead not created by this tool (no SPEC stamp) does not block the dialog. Its geometry is matched against
/// every Standard's SPECs: a unique match is treated as that SPEC, and anything else is shown as a warning.
/// Either way, applying a SPEC to it updates the feature to that SPEC's parameters and records the stamp.
///
/// The dialog has no status surface: what the ShtMetal tree used to show — body, thickness, material,
/// preferences, mode, allowed grades, errors and warnings — goes to the NX listing window through
/// <see cref="NxSessionContext.Log"/>. Anything that actually blocks Apply is still raised as a message box
/// when the user presses Apply.</summary>
public sealed class BeadDialogPresenter : IBeadTreeSink
{
    /// <summary>The material filter's first member, so the tree only ever lists a material the user chose.</summary>
    private const string ChooseMaterialOption = "(choose a material)";

    private readonly NxSessionContext _context;
    private readonly BlockAccessor _blocks;
    private readonly BeadSpecCache _specCache;
    private readonly BeadSpecValidator _validator;
    private readonly BeadSpecFinder _specFinder;
    private readonly SelectedCurveSetValidator _curveSetValidator;
    private readonly BeadSelectionExpander _selectionExpander;
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

    // The chosen Standard, the chosen bead SPEC's rows, and why there are none when there are none.
    private StandardInfo? _standard;
    private IReadOnlyList<BeadSpecRow> _currentSpecs = Array.Empty<BeadSpecRow>();
    private string? _noSpecsReason;

    // The chosen sheet metal material. Survives selection changes: it is the user's choice, not a fact of the body.
    private SheetMetalMaterialRow? _pickedRow;

    // The body the preferences' row was last preselected for, so re-selecting curves on it keeps the user's own pick.
    private BodyId? _preselectedFor;

    // Recomputed on every OnSelectionChanged.
    private Body? _resolvedBody;
    private ProfileReadOutcome? _outcome;
    private readonly List<CurveState> _perCurve = new();

    // The last status block written to the listing window. Render() runs on every picker change, so an
    // unchanged status is not written again — otherwise the listing window fills with duplicates and the one
    // line that did change is impossible to spot.
    private string? _lastStatus;

    // SPEC ids of selected beads whose bead SPEC could not be pinned down (older stamp, and the id is no longer
    // in exactly one workbook). Computed once per selection rather than in Warnings(), which Render() calls on
    // every picker change — resolving them goes to the spec cache, and that is a file read per SPEC.
    private IReadOnlyList<string> _unresolvedBeadSpecs = Array.Empty<string>();

    public BeadDialogPresenter(
        NxSessionContext context,
        BlockAccessor blocks,
        BeadSpecCache specCache,
        BeadSpecValidator validator,
        BeadSpecFinder specFinder,
        SelectedCurveSetValidator curveSetValidator,
        BeadSelectionExpander selectionExpander,
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
        _selectionExpander = selectionExpander;
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

    /// <summary>One selected item, what it traces back to, and — for a bead with no stamp — what its geometry
    /// was matched to.</summary>
    /// <param name="MatchedSpec">The SPEC an unstamped bead was identified as, or null.</param>
    /// <param name="UnmatchedReason">Why an unstamped bead could not be identified, or null.</param>
    private sealed record CurveState(NXObject Curve, CurveTraceback Traceback, BeadSpecRow? MatchedSpec, string? UnmatchedReason)
    {
        public bool IsUnstamped => Traceback.Result.HasUnstampedFeature;

        public bool IsExistingFeature => Traceback.ExistingFeature is not null;

        /// <summary>The Standard/SPEC this bead is known to be, from its stamp or its geometry, plus which bead
        /// SPEC it came from when that is known. <c>BeadSpec</c> is null for a bead stamped before the bead SPEC
        /// name was recorded; the caller looks it up from the SPEC id instead.</summary>
        public (string StandardId, string SpecId, string? BeadSpec)? KnownSpec =>
            Traceback.Result is { Found: true, StandardId: { } standardId, SpecId: { } specId }
                ? (standardId, specId, Traceback.Result.BeadSpec)
                : MatchedSpec is { } matched ? (matched.StandardId, matched.SpecId, matched.WorkbookName) : null;
    }

    /// <summary>The facts a SPEC is validated against: the body, made to the chosen sheet metal material. Null until
    /// both are known.</summary>
    private SheetMetalProfile? Profile => _outcome is { } outcome && _pickedRow is { } row ? outcome.ProfileFor(row) : null;

    /// <summary>Called from <c>initialize_cb</c>.</summary>
    public void Initialize()
    {
        // First: every block field in the accessor is null until this runs, and the tree's callbacks are
        // registered here too.
        _blocks.Initialize(this);

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
        RefreshMaterialFilter();
        LoadSelectedBeadSpec();

        // The first OnSelectionChanged runs from dialogShown_cb, not here: it fills the ShtMetal tree, and NX only
        // accepts tree columns once the dialog is shown.
    }

    // ---- Selection ----

    public void OnClearAllClicked()
    {
        _blocks.ClearSelection();
        OnSelectionChanged();
    }

    public void OnSelectionChanged()
    {
        var selection = _blocks.GetSelectedCurves();

        if (selection.Count == 0)
        {
            ClearBody();
            // The tree colours rows against the body's thickness, so it has to be redrawn once there is no body.
            RefreshMaterialTree();
            Render("Select a sheet metal body, or curve(s) on one, to begin.", errorText: null);
            return;
        }

        // The body is resolved from what the user actually picked — a body resolves to itself — while everything
        // downstream works on the expanded list, where a picked body has become the beads sitting on it.
        var bodyResult = _curveSetValidator.ResolveSingleBody(selection);
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
        RefreshMaterialTree();
        // After tracing: the list keeps the SPECs of the beads just selected, not the previous selection's.
        TraceAllCurves(_selectionExpander.Expand(selection));
        RepopulateSpecVariants();
        UpdateModeAndSpecPickers();
    }

    private void ClearBody()
    {
        _resolvedBody = null;
        _outcome = null;
        _perCurve.Clear();
        _unresolvedBeadSpecs = Array.Empty<string>();
        _blocks.SetSelectionInfo(Array.Empty<string>());
    }

    private void ResetToError(string? message)
    {
        ClearBody();
        RefreshMaterialTree();
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
            LoadSelectedBeadSpec();
        }

        // The Standard could not be selected (not listed): a row from another Standard would put a material the
        // filter does not offer into it, which NX refuses.
        if (!IsCurrentStandard(row.Standard))
            return;

        // Set before refreshing: the tree only lists one physical material's rows, and RefreshMaterialFilter
        // moves the filter to the picked row's material — so the preferred row is on show, and checked, without
        // a second write that would look like a user action.
        _pickedRow = row;
        RefreshMaterialFilter();
    }

    private void TraceAllCurves(IReadOnlyList<NXObject> selection)
    {
        _perCurve.Clear();

        // An unstamped bead is searched for across every Standard, and several selected curves can belong to
        // the same bead, so the spec list and each bead's identification are computed once per selection.
        IReadOnlyList<BeadSpecRow>? allSpecs = null;
        var identifiedByFeature = new Dictionary<Tag, (BeadSpecRow? Matched, string? Reason)>();

        foreach (var item in selection)
        {
            var traceback = _tracebackService.Trace(item);
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

            _perCurve.Add(new CurveState(item, traceback, matched, unmatchedReason));
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

        // Named by workbook and SPEC: two of a Standard's bead SPEC workbooks could carry the same SpecId, and
        // listing the bare ids would print the same thing twice with no way to tell them apart.
        return match.IsAmbiguous
            ? (null, $"its geometry matches several SPECs ({string.Join(", ", match.Candidates.Select(c => $"{c.WorkbookName}/{c.SpecId}"))})")
            : (null, "its geometry matches no SPEC in any Standard");
    }

    private void UpdateModeAndSpecPickers()
    {
        UpdateSelectionInfoList();

        var knownSpecs = KnownSpecs();

        _unresolvedBeadSpecs = knownSpecs
            .Where(k => IsCurrentStandard(k.StandardId) && ResolveBeadSpec(k.StandardId, k.SpecId, k.BeadSpec) is null)
            .Select(k => k.SpecId)
            .Distinct()
            .ToList();

        // Pre-fill the SPEC only when every known bead agrees on one SPEC in the chosen Standard — otherwise leave the
        // user's current choice alone. The Standard itself is never switched: it belongs to the sheet metal material.
        if (knownSpecs.Count == 1 && IsCurrentStandard(knownSpecs[0].StandardId))
        {
            var (_, specId, beadSpec) = knownSpecs[0];

            // The radio has to move first: the variants list only holds one bead SPEC's rows, so selecting the
            // variant before the workbook is loaded would select nothing.
            if (ResolveBeadSpec(knownSpecs[0].StandardId, specId, beadSpec) is { } resolved
                && !string.Equals(resolved, _blocks.GetSelectedBeadSpec(), StringComparison.OrdinalIgnoreCase))
            {
                _blocks.SelectBeadSpec(resolved);
                LoadSelectedBeadSpec();
                RepopulateSpecVariants();
            }

            if (_currentSpecs.FirstOrDefault(s => s.SpecId == specId) is { } specRow)
                _blocks.SelectSpecVariant(specRow);
        }

        // Re-validate the (possibly just pre-filled) SPEC — a traced-back SPEC can fail if the sheet metal changed
        // since it was created, and that must surface the same "failed rule + alternatives" error a user-driven pick
        // shows.
        Render();
    }

    /// <summary>Which bead SPEC a known bead belongs to: the stamp says so outright for anything this version
    /// built, and for an older stamp the SPEC id is looked up instead.
    ///
    /// Null when it cannot be pinned down — the SPEC has left its workbook, or (bad data) sits in two of them.
    /// The caller then leaves the radio where the user had it; guessing would quietly point the dialog at the
    /// wrong workbook, and the warning in <see cref="Warnings"/> says so.</summary>
    private string? ResolveBeadSpec(string standardId, string specId, string? stamped)
    {
        if (!string.IsNullOrEmpty(stamped))
            return MatchBeadSpecName(stamped!);

        return _specLookup.Find(standardId, specId) is { } row ? MatchBeadSpecName(row.WorkbookName) : null;
    }

    /// <summary>Maps a workbook name onto the bead SPEC the dialog offers, by the same "the file name contains
    /// the SPEC name" rule the workbook was found by. A name that already is one of the dialog's own (what the
    /// stamp records) matches itself.
    ///
    /// Longest match wins, so if one SPEC name is ever a substring of another the more specific one is chosen
    /// rather than whichever the .dlx happens to list first.</summary>
    private string? MatchBeadSpecName(string workbookOrSpecName) =>
        _blocks.GetBeadSpecNames()
            .Where(name => workbookOrSpecName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(name => name.Length)
            .FirstOrDefault();

    private List<(string StandardId, string SpecId, string? BeadSpec)> KnownSpecs() =>
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
        RefreshMaterialFilter();
        RefreshMaterialTree();
        LoadSelectedBeadSpec();
        RepopulateSpecVariants();
        Render();
    }

    /// <summary>Called from <c>update_cb</c> for the material filter. Narrowing the tree drops the picked row
    /// whenever it is no longer one of the rows on show — a checkbox the user cannot see must not still be the
    /// thing Apply builds to.</summary>
    public void OnMaterialFilterChanged()
    {
        if (_pickedRow is { } picked && !string.Equals(picked.PhysicalMaterialName, _blocks.GetSelectedMaterialFilter(), StringComparison.OrdinalIgnoreCase))
            _pickedRow = null;

        RefreshMaterialTree();
        RepopulateSpecVariants();
        Render();
    }

    /// <summary>Called from the ShtMetal tree via <see cref="IBeadTreeSink"/> when a row's checkbox is clicked.
    /// The check is radio-like: exactly one row is checked once any is, so clicking the checked row re-asserts
    /// it rather than clearing it. Every row's state is written back, even on the no-op path, so the tree shows
    /// exactly the picked row whatever NX did to the clicked node.</summary>
    public void OnMaterialRowChecked(SheetMetalMaterialRow? row)
    {
        if (row is not null)
            _pickedRow = row;

        _blocks.SetCheckedMaterialRow(_pickedRow);

        if (row is null)
            return;

        RepopulateSpecVariants();
        Render();
    }

    /// <summary>Reads the chosen Standard. A picked row from another Standard is dropped: the material and the
    /// Standard must agree.</summary>
    private void LoadSelectedStandard()
    {
        var standardId = _blocks.GetSelectedStandardId();
        _standard = _standards.FirstOrDefault(s => s.Id == standardId);

        if (_pickedRow is not null && !IsCurrentStandard(_pickedRow.Standard))
            _pickedRow = null;
    }

    /// <summary>The physical materials the chosen Standard offers, behind a "choose" prompt. The tree lists one
    /// material's rows at a time, so this is what decides which rows are on show at all.</summary>
    private void RefreshMaterialFilter()
    {
        var materials = CurrentStandardRows()
            .Select(r => r.PhysicalMaterialName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _blocks.PopulateMaterialFilter(new[] { ChooseMaterialOption }.Concat(materials).ToList());
        _blocks.SelectMaterialFilter(_pickedRow?.PhysicalMaterialName ?? ChooseMaterialOption);
    }

    /// <summary>Lists the chosen material's rows. Every row is offered; one whose thickness differs from the body
    /// is coloured, and picking it is reported rather than hidden.</summary>
    private void RefreshMaterialTree()
    {
        var filter = _blocks.GetSelectedMaterialFilter();
        IReadOnlyList<SheetMetalMaterialRow> rows = filter is null or ChooseMaterialOption
            ? Array.Empty<SheetMetalMaterialRow>()
            : CurrentStandardRows()
                .Where(r => string.Equals(r.PhysicalMaterialName, filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        _blocks.PopulateMaterialTree(rows, _pickedRow, ThicknessDiffers);
    }

    private IReadOnlyList<SheetMetalMaterialRow> CurrentStandardRows() =>
        _standard is null ? Array.Empty<SheetMetalMaterialRow>() : _materialTable.RowsFor(_standard.Id);

    private bool ThicknessDiffers(SheetMetalMaterialRow row) =>
        _outcome is { } outcome && !SheetMetalPreferenceCheck.ThicknessMatches(row.Thickness, outcome.Thickness);

    private bool PhysicalMaterialDiffers(SheetMetalMaterialRow row) =>
        _outcome is { PhysicalMaterialName: { } bodyMaterial }
        && !string.Equals(bodyMaterial, row.PhysicalMaterialName, StringComparison.OrdinalIgnoreCase);

    // ---- Bead SPEC ----

    /// <summary>Called from <c>update_cb</c> for the bead SPEC radio.</summary>
    public void OnBeadSpecChanged()
    {
        LoadSelectedBeadSpec();
        RepopulateSpecVariants();
        Render();
    }

    /// <summary>Reads the chosen bead SPEC's workbook, and why it has no SPECs when it has none.</summary>
    private void LoadSelectedBeadSpec()
    {
        _currentSpecs = Array.Empty<BeadSpecRow>();
        _noSpecsReason = null;

        if (_standard is null)
            return;

        var beadSpec = _blocks.GetSelectedBeadSpec();
        if (string.IsNullOrEmpty(beadSpec))
        {
            _noSpecsReason = "no bead SPEC is selected";
            return;
        }

        try
        {
            _currentSpecs = _specCache.GetSpecs(_standard, beadSpec!);
            if (_currentSpecs.Count == 0)
            {
                _noSpecsReason = _specCache.FindWorkbook(_standard, beadSpec!) is null
                    ? $"no workbook (.xlsx) in '{_standard.BeadSpecFolder}' is named for bead SPEC '{beadSpec}'"
                    : $"the bead SPEC workbook for '{beadSpec}' lists no SPECs";
            }
        }
        catch (Exception ex)
        {
            _noSpecsReason = $"its bead SPEC workbook could not be read: {ex.Message}";
        }
    }

    /// <summary>The variants list holds the chosen workbook's rows that validate against the current sheet metal.
    /// A SPEC a selected bead is already built to is kept on the list even when it no longer validates — otherwise
    /// it would have nothing to select, and the reason it fails would have no way to become visible.</summary>
    private void RepopulateSpecVariants()
    {
        // Rebuilding the list clears its selection, so the user's current SPEC is carried across whenever it
        // is still on the new list — otherwise checking a different material row would silently throw away a
        // SPEC they had already chosen.
        var previous = SelectedSpec()?.SpecId;

        var specs = Profile is { } profile
            ? _specFinder.FindValid(profile, _currentSpecs).ToList()
            : _currentSpecs.ToList();

        foreach (var (_, specId, _) in KnownSpecs())
        {
            if (specs.All(s => s.SpecId != specId)
                && _currentSpecs.FirstOrDefault(s => s.SpecId == specId) is { } known)
            {
                specs.Add(known);
            }
        }

        _blocks.PopulateSpecVariants(specs);

        if (previous is not null && specs.FirstOrDefault(s => s.SpecId == previous) is { } stillThere)
            _blocks.SelectSpecVariant(stillThere);
    }

    /// <summary>Called from <c>update_cb</c> for the SPEC variants list.</summary>
    public void OnSpecVariantChanged() => Render();

    private BeadSpecRow? SelectedSpec() => _blocks.GetSelectedSpecVariant();

    // ---- State and rendering ----

    /// <summary>What the dialog is doing, and the one thing stopping Apply, if any — in the order the user has to
    /// resolve them.</summary>
    private (string ModeText, string? ErrorText) CurrentModeAndErrorText()
    {
        if (_outcome is not { } outcome)
            return ("Select a sheet metal body, or curve(s) on one, to begin.", null);

        if (_standard is null)
            return ("Choose a Standard.", null);

        if (_pickedRow is not { } row)
            return ("Choose a material, then check one of its rows.", null);

        if (ThicknessDiffers(row))
        {
            return (CurrentModeText(),
                $"Sheet metal material '{row.Name}' is {row.Thickness:0.####} thick, but this sheet metal is " +
                $"{outcome.Thickness:0.####}. Check a material row of this thickness, or correct the body's thickness.");
        }

        if (_noSpecsReason is not null)
            return (CurrentModeText(), $"No bead SPEC is allowed under Standard '{_standard.Id}': {_noSpecsReason}.");

        var spec = SelectedSpec();
        if (spec is null)
            return ("Choose a SPEC from the bead options.", null);

        var profile = outcome.ProfileFor(row);
        var result = _validator.Validate(profile, spec);
        if (result.IsValid)
            return (CurrentModeText(), null);

        var alternatives = _specFinder.FindValid(profile, _currentSpecs).Select(s => s.SpecId).ToList();
        var alternativesText = alternatives.Count > 0
            ? $" Valid alternatives under this bead SPEC: {string.Join(", ", alternatives)}."
            : " No SPECs under this bead SPEC currently validate against this sheet metal.";

        return (CurrentModeText(), $"{result.BlockingMessage}{alternativesText}");
    }

    private string CurrentModeText()
    {
        var updateCount = _perCurve.Count(s => s.IsExistingFeature);
        var createCount = _perCurve.Count - updateCount;
        return updateCount switch
        {
            0 when createCount == 0 => "Nothing selected to build: the chosen body has no beads on it.",
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

    /// <summary>Writes the current state to the listing window and refreshes the SPEC preview. The dialog has no
    /// status block, so this is where body/thickness/material/preferences/mode/error/warning surface.</summary>
    private void Render(string modeText, string? errorText)
    {
        var spec = SelectedSpec();
        _blocks.SetSpecPreview(spec?.RadiusAndRadS, spec?.Width, spec?.Height, spec?.DieRadiusP);

        var lines = new List<string>
        {
            $"Body: {_resolvedBody?.Name ?? "(no selection)"}",
            $"Thickness: {(_outcome is { } o ? $"{o.Thickness:0.###}" : "(unknown)")}",
        };

        if (_resolvedBody is not null)
            lines.Add($"Material: {_outcome?.PhysicalMaterialName ?? "(none — applied from the sheet metal material)"}");

        if (SheetMetalMaterialText() is { } materialText)
            lines.Add($"Sheet Metal Material: {materialText}");

        if (PreferencesText() is { } preferencesText)
            lines.Add($"Preferences: {preferencesText}");

        lines.Add($"Mode: {modeText}");

        if (spec is not null)
        {
            lines.Add($"SPEC Thickness: {spec.Thickness:0.###}");
            lines.Add($"Allowed Materials: {string.Join(", ", spec.AllowedMaterialGrades.Where(kv => kv.Value).Select(kv => kv.Key))}");
        }

        var status = string.Join(Environment.NewLine, lines);
        var warnings = Warnings();

        // Only when something actually changed: Render runs on every picker change, and an unchanged block
        // repeated on each click would bury the line that did change.
        var fingerprint = $"{status}|{errorText}|{warnings}";
        if (fingerprint == _lastStatus)
            return;

        _lastStatus = fingerprint;
        _context.Log.Info(status);

        if (warnings is not null)
            _context.Log.Warn(warnings);

        if (!string.IsNullOrEmpty(errorText))
            _context.Log.Warn($"Blocking: {errorText}");
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

        // A stamped bead whose bead SPEC could not be pinned down: the radio was deliberately left alone rather
        // than pointed at a guessed workbook, so say so instead of letting the user wonder why it did not move.
        if (_unresolvedBeadSpecs.Count > 0)
        {
            warnings.Add(
                $"SPEC {string.Join(", ", _unresolvedBeadSpecs)} could not be matched to one of this dialog's bead SPECs — " +
                "the bead was stamped before the bead SPEC was recorded, and its SPEC id is no longer in exactly one " +
                "workbook. Choose the bead SPEC yourself; Apply then records it.");
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
            _blocks.ShowError("Select a sheet metal body, or curve(s) on one, first.");
            return 1;
        }

        if (_pickedRow is not { } row)
        {
            _blocks.ShowError("Choose a Standard and a material, then check one of its rows.");
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
            _blocks.ShowError("Choose a SPEC from the bead options first.");
            return 1;
        }

        if (_perCurve.Count == 0)
        {
            _blocks.ShowError("Nothing is selected to build. Select curve(s) for a new bead, or a body that has beads on it.");
            return 1;
        }

        // Replacing a different material is confirmed by the engine's own reassignment rule, inside Assign below.
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
            var assigned = _materialAssignment.Assign(BodyResolver.GetBodyId(_resolvedBody), material, row, _blocks.Confirm);
            if (!assigned.Ok)
            {
                // Declining the engine's own confirmation is the user's choice, not an error to report back to them.
                if (assigned.ErrorCode != "ASSIGNMENT_DECLINED")
                    _blocks.ShowError(assigned.Message ?? "Material assignment failed. No changes were made.");
                return 1;
            }

            warnings.AddRange(assigned.Value ?? Array.Empty<string>());
        }

        // Assign above already set the preferences to this row; only an unchanged material leaves them to set here.
        if (material is null && !outcome.Preference.UsesMaterial(row.Name))
        {
            var synced = _preferences.SyncMaterial(row);
            if (!synced.Ok)
            {
                _blocks.ShowError($"{synced.Message ?? "Sheet Metal Preferences could not be updated."} No changes were made.");
                return 1;
            }
        }

        // The bead SPEC recorded on the bead is the one the dialog offers, not the workbook's file name, so a
        // file rename or a revision suffix cannot orphan a stamped bead.
        var beadSpec = MatchBeadSpecName(spec.WorkbookName) ?? spec.WorkbookName;
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

            BeadAttributeWriter.Stamp(result.Value!, spec.StandardId, beadSpec, spec.SpecId, isNewFeature: !isUpdate);
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
