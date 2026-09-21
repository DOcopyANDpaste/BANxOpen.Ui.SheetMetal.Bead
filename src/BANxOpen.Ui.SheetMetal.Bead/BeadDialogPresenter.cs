using BANxOpen.SheetMetal.Beads;
using BANxOpen.SheetMetal.SpecData;
using NXOpen;
using NXOpen.Features;
using NXOpen.Features.SheetMetal;
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
/// The user selects in two blocks: curves in the curve block (a Super Section, which can also draw a sketch on the
/// fly), and existing Bead features in the feature block. Each connected chain the curve block collects is one bead. A chain
/// whose curves all belong to one existing bead updates that bead; one that spans several beads, or mixes a bead's
/// curves with new ones, is listed as an error and blocks Apply. The selection list shows one line per bead the
/// builder works on, with no bead twice however it was picked. Anything else, and any duplicate, is left out of
/// the list and only traced.
///
/// On open the dialog reads the part: its Sheet Metal Preferences (which preselect the Standard and row), and its
/// solid bodies. With exactly one, that body is resolved up front and checked against the preferences
/// (<see cref="SheetMetalPreferenceCheck"/>); with more, the body is resolved from the selection.
///
/// The user picks a sheet metal material in three steps: a Standard, then a physical material within it, then
/// one of that material's rows in the tree — the row carries the grade, the thickness and the bend radius, and
/// only a row is specific enough to judge a SPEC by. Exactly one row is ever checked. The Standard is chosen
/// once for the part: it is the Standard of the row the part's Sheet Metal Preferences are set to, and is
/// preselected from them.
/// TODO(business): confirm the Standard is part-level rather than chosen per bead.
///
/// Until a body is known, SPECs are validated against the preferences' thickness, so the BeadOptions tree is
/// filled from the moment the dialog opens.
///
/// Every step that decides what the pickers show writes a <c>[TRACE]</c> line to the listing window.
/// TODO: gate or remove the trace once the dialog is signed off.
///
/// The bead SPEC is chosen separately: the <c>enum_BABead</c> radio names one of the Standard's bead SPEC
/// workbooks, and that workbook's rows — filtered to the ones that validate against the chosen material — fill
/// the BeadOptions tree, where one SPEC is checked the same way as a material row. The radio's members come from the .dlx, not from code.
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
/// The dialog's one status surface is <c>label_currentPref</c>: the part's Sheet Metal Preferences and the row
/// picked in the tree. Everything else — body, thickness, material, mode, allowed grades, errors and warnings —
/// goes to the NX listing window through <see cref="NxSessionContext.Log"/>. Anything that actually blocks Apply
/// is still raised as a message box when the user presses Apply.
///
/// The selection list holds one line per connected chain (one bead) or picked Bead feature, and its delete
/// button takes that bead out of the selection blocks. Every bead is built to the one side the direction block
/// says, existing beads included. With Show Preview on, NX previews every bead the dialog would build, from
/// builders that are never committed.
///
/// Every callback runs through <see cref="Guard"/>: an exception is logged in full and the trees are rebuilt
/// from the presenter's state, rather than leaving a half-updated dialog.</summary>
public sealed class BeadDialogPresenter : IBeadTreeSink, IDisposable
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
    private readonly BeadDirectionProbe _directionProbe;

    private IReadOnlyList<StandardInfo> _standards = Array.Empty<StandardInfo>();

    // The chosen Standard, the chosen bead SPEC's rows, and why there are none when there are none.
    private StandardInfo? _standard;
    private IReadOnlyList<BeadSpecRow> _currentSpecs = Array.Empty<BeadSpecRow>();
    private string? _noSpecsReason;

    // The chosen sheet metal material. Survives selection changes: it is the user's choice, not a fact of the body.
    private SheetMetalMaterialRow? _pickedRow;

    // The SPEC checked in the BeadOptions tree — what Apply builds to. Kept across repopulates while still listed.
    private BeadSpecRow? _pickedSpec;

    // The live previews, one per bead, while Show Preview is on.
    private readonly List<BeadPreview> _previews = new();

    // The part's Sheet Metal Preferences, read on open and after each Apply. Null when they could not be read.
    private SheetMetalPartPreference? _partPreference;

    // The part's one solid body and its profile, when it has exactly one — resolved on open, and what the dialog
    // falls back to whenever the selection is empty.
    private Body? _bodyOnOpen;
    private ProfileReadOutcome? _outcomeOnOpen;

    // Recomputed on every OnSelectionChanged.
    private Body? _resolvedBody;
    private ProfileReadOutcome? _outcome;
    private readonly List<CurveState> _perCurve = new();

    // What each line of the selection list was picked as — the curves and/or Bead features, duplicates of the
    // same bead included — so the list's delete button can take exactly those out of the selection blocks.
    private readonly List<List<NXObject>> _rowSources = new();

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
        SheetMetalMaterialTable materialTable,
        BeadDirectionProbe directionProbe)
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
        _directionProbe = directionProbe;
    }

    /// <summary>One bead to build or update — a chain of curves, or a picked Bead feature — what it traces back to,
    /// and, for a bead with no stamp, what its geometry was matched to.</summary>
    /// <param name="Chain">The curves a new bead is built from; empty for a picked Bead feature.</param>
    /// <param name="FromSketch">The sketch every curve of the chain belongs to, for its label.</param>
    /// <param name="MatchedSpec">The SPEC an unstamped bead was identified as, or null.</param>
    /// <param name="UnmatchedReason">Why an unstamped bead could not be identified, or null.</param>
    /// <param name="ChainError">Why the chain cannot be built as one bead — its curves belong to several beads, or
    /// mix a bead's curves with new ones. Such an entry is listed, and blocks Apply.</param>
    private sealed record CurveState(
        IReadOnlyList<NXObject> Chain, Sketch? FromSketch, CurveTraceback Traceback,
        BeadSpecRow? MatchedSpec, string? UnmatchedReason, string? ChainError = null)
    {
        public bool IsUnstamped => Traceback.Result.HasUnstampedFeature;

        public bool IsExistingFeature => Traceback.ExistingFeature is not null;

        /// <summary>What the bead builder works on — the existing Bead feature (whose curves it ignores), or else
        /// the chain a new bead is built from — as a key: the selection list holds one line per key.</summary>
        public string BuildTargetKey => Traceback.ExistingFeature is { } feature
            ? $"F:{feature.Tag}"
            : "C:" + string.Join(",", Chain.Select(c => c.Tag.ToString()).OrderBy(t => t, StringComparer.Ordinal));

        /// <summary>An object that stands for the build target, for its journal identifier.</summary>
        public NXObject BuildTarget => Traceback.ExistingFeature ?? Chain[0];

        /// <summary>The Standard/SPEC this bead is known to be, from its stamp or its geometry, plus which bead
        /// SPEC it came from when that is known. <c>BeadSpec</c> is null for a bead stamped before the bead SPEC
        /// name was recorded; the caller looks it up from the SPEC id instead.</summary>
        public (string StandardId, string SpecId, string? BeadSpec)? KnownSpec =>
            Traceback.Result is { Found: true, StandardId: { } standardId, SpecId: { } specId }
                ? (standardId, specId, Traceback.Result.BeadSpec)
                : MatchedSpec is { } matched ? (matched.StandardId, matched.SpecId, matched.WorkbookName) : null;
    }

    /// <summary>The facts a SPEC is validated against: the body, made to the chosen sheet metal material. Before a body
    /// is known, the part's Sheet Metal Preferences stand in for it — they carry the thickness NX builds the sheet
    /// metal to. Null until a row is picked.</summary>
    private SheetMetalProfile? Profile =>
        _pickedRow is not { } row ? null
        : _outcome is { } outcome ? outcome.ProfileFor(row)
        : _partPreference is { } preference ? new SheetMetalProfile(PreferencesProfileId, PreferencesProfileName, preference.Thickness, row.Grade)
        : null;

    // No bead rule reads the body identity; these only label a profile that stands for the preferences, not a body.
    private static readonly BodyId PreferencesProfileId = new("(sheet metal preferences)");
    private const string PreferencesProfileName = "(Sheet Metal Preferences)";

    /// <summary>The thickness SPECs and rows are judged against: the body's once one is known, else the
    /// preferences'.</summary>
    private double? CurrentThickness => _outcome?.Thickness ?? _partPreference?.Thickness;

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
            Trace($"ListStandards threw: {ex}");
            _blocks.ShowError($"Could not list the Standards in the sheet metal material standards file: {ex.Message}");
            _standards = Array.Empty<StandardInfo>();
        }

        Trace($"Standards listed: {_standards.Count} [{string.Join(", ", _standards.Select(s => s.Id))}]");

        _blocks.PopulateStandards(_standards.Select(s => (s.Id, s.DisplayName)).ToList());
        LoadSelectedStandard();
        RefreshMaterialFilter();
        LoadSelectedBeadSpec();

        ReadPart();
        PreselectFromPreferences();
        UseBodyFoundOnOpen();
        RepopulateSpecVariants();

        // The first selection refresh runs from dialogShown_cb, not here: it fills both trees, and NX only accepts
        // tree columns once the dialog is shown.
    }

    /// <summary>Called from <c>dialogShown_cb</c>.</summary>
    public void OnDialogShown() => Guard("dialog shown", () =>
    {
        _blocks.MarkShown();
        RefreshSelection();
    });

    /// <summary>Called from <c>cancel_cb</c>: nothing is built, so only the preview has to go.</summary>
    public void OnCancel() => DisposePreviews();

    /// <summary>Called once the dialog has closed, however it closed.</summary>
    public void Dispose() => DisposePreviews();

    /// <summary>Runs one dialog callback. An exception is logged in full — which callback, and the whole stack —
    /// then the view is rebuilt from the presenter's state, which the failure did not touch. Without this, one
    /// throw half-way through a refresh leaves the trees empty or out of step with what Apply would build.</summary>
    private void Guard(string callback, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _context.Log.Error($"[{callback}] failed; the dialog is being redrawn from its last state. Please report this: {ex}");
            RecoverView();
        }
    }

    private void RecoverView()
    {
        try
        {
            DisposePreviews();
            _blocks.RebuildStaleTrees();
            RefreshMaterialTree();
            RepopulateSpecVariants();
            UpdateSelectionInfoList();
            _lastStatus = null;
            Render();
        }
        catch (Exception ex)
        {
            _context.Log.Error($"Redrawing the dialog after an error failed too: {ex}");
        }
    }

    // ---- The part: preferences and its one body ----

    /// <summary>Reads what the dialog knows before anything is selected: the part's Sheet Metal Preferences, and —
    /// when the part has exactly one solid body — that body, checked against them. Nothing here blocks: what is
    /// wrong goes to the listing window, and Apply still refuses what it always refused.</summary>
    private void ReadPart()
    {
        var read = _preferences.ReadForPart();
        _partPreference = read.Preference;

        if (_partPreference is { } preference)
        {
            Trace($"Sheet Metal Preferences: material '{preference.MaterialName ?? "(none)"}', " +
                  $"Material Table entry {preference.IsMaterialTableEntry}, thickness {preference.Thickness:0.####}, " +
                  $"row {(preference.Row is { } row ? $"'{row.Name}' (Standard {row.Standard})" : "not in the standards file")}, " +
                  $"{preference.SheetMetalBodyCount} sheet metal body(ies)");
        }
        else
        {
            _context.Log.Warn($"Could not read this part's Sheet Metal Preferences: {read.ReadError ?? "no preferences were returned"}.");
        }

        _bodyOnOpen = null;
        _outcomeOnOpen = null;

        var solidBodies = SolidBodies();
        Trace($"Solid bodies in the part: {solidBodies.Count}");

        if (solidBodies.Count > 1)
        {
            _context.Log.Warn($"This part has {solidBodies.Count} solid bodies. The sheet metal body is resolved from the selection.");
            return;
        }

        if (solidBodies.Count == 0)
            return;

        var body = solidBodies[0];
        var profile = _profileReader.ReadFor(body);
        if (!profile.Ok)
        {
            _context.Log.Error(profile.Message ?? $"Could not read the sheet metal of body '{body.Name}'.");
            return;
        }

        _bodyOnOpen = body;
        _outcomeOnOpen = profile.Value!;
        Trace($"Body on open: '{_outcomeOnOpen.BodyName}', thickness {_outcomeOnOpen.Thickness:0.####}, " +
              $"material '{_outcomeOnOpen.PhysicalMaterialName ?? "(none)"}'");

        if (_partPreference is { } partPreference)
        {
            var check = SheetMetalPreferenceCheck.Evaluate(_outcomeOnOpen.PhysicalMaterialName, _outcomeOnOpen.Thickness, partPreference);
            if (check.Status == SheetMetalPreferenceStatus.InSync)
                Trace("Body matches the Sheet Metal Preferences.");
            else
                _context.Log.Warn($"Body '{_outcomeOnOpen.BodyName}' does not match the Sheet Metal Preferences: {check.Message}");
        }
    }

    private List<Body> SolidBodies()
    {
        try
        {
            return _context.WorkPart.Bodies.Cast<Body>().Where(b => b.IsSolidBody).ToList();
        }
        catch (NXException ex)
        {
            _context.Log.Error($"Could not list the part's bodies: NX {ex.ErrorCode}: {ex.Message}");
            return new List<Body>();
        }
    }

    private void UseBodyFoundOnOpen()
    {
        _resolvedBody = _bodyOnOpen;
        _outcome = _outcomeOnOpen;
    }

    /// <summary>The part's Sheet Metal Preferences say which Standard and row it is already made to, so the dialog
    /// opens on them. Runs once, on open: after that the pick is the user's.</summary>
    private void PreselectFromPreferences()
    {
        if (_partPreference is not { IsMaterialTableEntry: true, Row: { } row })
        {
            Trace("Preselect: skipped (preferences not Material Table entry, or their material is not in the standards file).");
            return;
        }

        if (_standard is null || !string.Equals(_standard.Id, row.Standard, StringComparison.OrdinalIgnoreCase))
        {
            _blocks.SelectStandard(row.Standard);
            LoadSelectedStandard();
            LoadSelectedBeadSpec();
        }

        // The Standard could not be selected (not listed): a row from another Standard would put a material the
        // filter does not offer into it, which NX refuses.
        if (!IsCurrentStandard(row.Standard))
        {
            Trace($"Preselect: Standard '{row.Standard}' of row '{row.Name}' is not listed; nothing preselected.");
            return;
        }

        // Set before refreshing: the tree only lists one physical material's rows, and RefreshMaterialFilter
        // moves the filter to the picked row's material — so the preferred row is on show, and checked, without
        // a second write that would look like a user action.
        _pickedRow = row;
        RefreshMaterialFilter();
        Trace($"Preselect: Standard '{row.Standard}', material '{row.PhysicalMaterialName}', row '{row.Name}'.");
    }

    // ---- Selection ----

    public void OnClearAllClicked() => Guard("Clear Selection", () =>
    {
        _blocks.ClearSelection();
        RefreshSelection();
    });

    /// <summary>Called from <c>update_cb</c> for the Bead feature block.</summary>
    public void OnSelectionChanged() => Guard("feature selection", RefreshSelection);

    /// <summary>Called from <c>update_cb</c> for the curve block — also when a sketch drawn on the fly in it is
    /// finished or cancelled. The preview is taken down first: its builders must not outlive curves the sketch
    /// task just rolled back.</summary>
    public void OnCurveSelectionChanged() => Guard("curve selection", () =>
    {
        DisposePreviews();
        RefreshSelection();
    });

    /// <summary>The selection list's delete button: the chosen lines' beads leave the selection. Both selection
    /// blocks are re-set from what the remaining lines were picked as. The curve block only takes Sections, so each
    /// remaining line's curves go back as a Section of their own — one chain, one bead, as before.</summary>
    public void OnSelectionRowsDeleted(IReadOnlyList<int> indices) => Guard("delete from selection", () =>
    {
        var deleted = new HashSet<int>(indices.Where(i => i >= 0 && i < _rowSources.Count));
        if (deleted.Count == 0)
            return;

        var remaining = _rowSources.Where((_, i) => !deleted.Contains(i)).ToList();
        var features = remaining.SelectMany(s => s).OfType<Feature>().Distinct().Cast<TaggedObject>().ToList();
        var chains = remaining
            .Select(s => s.Where(o => o is not Feature).Distinct().ToList())
            .Where(chain => chain.Count > 0)
            .ToList();
        Trace($"Deleting {deleted.Count} line(s) from the selection; {chains.Count} chain(s) and {features.Count} feature(s) stay selected.");

        DisposePreviews();
        // Features first: should the curve block refuse its Sections, the feature block is still up to date.
        _blocks.SetBeadFeatureBlockObjects(features);
        _blocks.SetCurveBlockObjects(chains.Select(chain => (TaggedObject)CurveSectionFactory.Create(_context.WorkPart, chain)).ToList());
        RefreshSelection();
    });

    private void RefreshSelection()
    {
        var curveObjects = _blocks.GetCurveBlockObjects();
        var featureObjects = _blocks.GetBeadFeatureBlockObjects();
        Trace($"Curve block: {curveObjects.Count} object(s) [{string.Join(", ", curveObjects.Select(Describe))}]");
        Trace($"Bead feature block: {featureObjects.Count} object(s) [{string.Join(", ", featureObjects.Select(Describe))}]");

        var expanded = _selectionExpander.Expand(curveObjects, featureObjects);
        foreach (var note in expanded.Notes)
            Trace($"Curve block → {note}");
        foreach (var rejected in expanded.Rejected)
            Trace($"Left out (not a curve or a Bead feature): {Describe(rejected)}");
        Trace($"Expanded to {expanded.Items.Count} item(s).");

        if (expanded.Items.Count == 0)
        {
            ClearBody();
            UseBodyFoundOnOpen();
            _blocks.SetSelectionInfo(Array.Empty<string>());
            // The tree colours rows against the body's thickness, so it has to be redrawn once the body changes.
            RefreshMaterialTree();
            RepopulateSpecVariants();
            Render();
            return;
        }

        var bodyResult = _curveSetValidator.ResolveSingleBody(
            expanded.Items.SelectMany(i => i.BeadFeature is { } feature ? new NXObject[] { feature } : i.Curves).ToList());
        if (!bodyResult.Ok)
        {
            ResetToError(expanded.Items, bodyResult.Message);
            return;
        }

        var profileResult = _profileReader.ReadFor(bodyResult.Value!);
        if (!profileResult.Ok)
        {
            ResetToError(expanded.Items, profileResult.Message);
            return;
        }

        _resolvedBody = bodyResult.Value;
        _outcome = profileResult.Value!;
        Trace($"Resolved body '{_outcome.BodyName}', thickness {_outcome.Thickness:0.####}.");

        RefreshMaterialTree();
        // After tracing: the list keeps the SPECs of the beads just selected, not the previous selection's.
        TraceAllCurves(expanded.Items);
        RepopulateSpecVariants();
        UpdateModeAndSpecPickers();
    }

    private void ClearBody()
    {
        _resolvedBody = null;
        _outcome = null;
        _perCurve.Clear();
        _rowSources.Clear();
        _unresolvedBeadSpecs = Array.Empty<string>();
    }

    /// <summary>What one expanded item was picked as: its curves, or the Bead feature itself.</summary>
    private static List<NXObject> SourcesOf(ExpandedSelectionItem item) =>
        item.BeadFeature is { } feature ? new List<NXObject> { feature } : item.Curves.ToList();

    /// <summary>The selection cannot be built from. What was selected stays listed, each line saying why, rather
    /// than the list emptying and the reason only reaching the listing window.</summary>
    private void ResetToError(IReadOnlyList<ExpandedSelectionItem> items, string? message)
    {
        Trace($"Selection error: {message}");
        ClearBody();

        var lines = UniqueLabels(items
                .Select(i => i.BeadFeature is { } feature
                    ? (Target: (NXObject)feature, Label: FeatureLabel(feature))
                    : (Target: i.Curves[0], Label: ChainLabel(i.Curves, i.FromSketch)))
                .ToList())
            .Select(label => $"{label} — ERROR: {message}")
            .ToList();
        _rowSources.AddRange(items.Select(SourcesOf));
        _blocks.SetSelectionInfo(lines);

        RefreshMaterialTree();
        RepopulateSpecVariants();
        Render("Selection error", message);
    }

    private void TraceAllCurves(IReadOnlyList<ExpandedSelectionItem> selection)
    {
        _perCurve.Clear();
        _rowSources.Clear();

        // An unstamped bead is searched for across every Standard, and several selected curves can belong to
        // the same bead, so the spec list and each bead's identification are computed once per selection.
        IReadOnlyList<BeadSpecRow>? allSpecs = null;
        var identifiedByFeature = new Dictionary<Tag, (BeadSpecRow? Matched, string? Reason)>();

        // One entry per build target: a Bead feature picked directly and a chain of its own section curves, or two
        // pattern members of one stamped original, all come down to one thing for the builder to work on.
        // Build target key -> its line, so a duplicate's objects are deleted along with the line it folded into.
        var targets = new Dictionary<string, int>();

        foreach (var item in selection)
        {
            var state = item.BeadFeature is { } picked
                ? new CurveState(Array.Empty<NXObject>(), null, _tracebackService.Trace(picked), null, null)
                : TraceChain(item.Curves, item.FromSketch);

            if (targets.TryGetValue(state.BuildTargetKey, out var line))
            {
                Trace($"Left out (already listed): {TargetLabel(state)}");
                _rowSources[line].AddRange(SourcesOf(item));
                continue;
            }

            targets[state.BuildTargetKey] = _perCurve.Count;
            _rowSources.Add(SourcesOf(item));

            if (state.Traceback.Result.HasUnstampedFeature && state.Traceback.ExistingFeature is { } feature)
            {
                if (!identifiedByFeature.TryGetValue(feature.Tag, out var identified))
                {
                    identified = Identify(feature, ref allSpecs);
                    identifiedByFeature[feature.Tag] = identified;
                }

                state = state with { MatchedSpec = identified.Matched, UnmatchedReason = identified.Reason };
            }

            _perCurve.Add(state);
        }

        Trace($"Build targets: {_perCurve.Count} ({_perCurve.Count(s => s.IsExistingFeature)} existing bead(s), " +
              $"{_perCurve.Count(s => s.ChainError is not null)} with an error).");
    }

    /// <summary>A chain is one bead. When none of its curves belongs to a bead it is a new one; when all of them
    /// belong to the same bead, that bead is updated. Anything else — curves of several beads, or a bead's curves
    /// mixed with new ones — cannot be one bead, and says so.</summary>
    private CurveState TraceChain(IReadOnlyList<NXObject> chain, Sketch? fromSketch)
    {
        var tracebacks = chain.Select(curve => _tracebackService.Trace(curve)).ToList();
        var beads = tracebacks
            .Where(t => t.ExistingFeature is not null)
            .GroupBy(t => t.ExistingFeature!.Tag)
            .Select(g => g.First())
            .ToList();
        var newCurves = tracebacks.Count(t => t.ExistingFeature is null);

        string? error = beads.Count switch
        {
            0 => null,
            1 when newCurves == 0 => null,
            1 => $"mixes curves of bead {FeatureLabel(beads[0].ExistingFeature!)} with {newCurves} curve(s) of no bead — select one bead's curves, or only new curves",
            _ => $"its curves belong to beads {string.Join(", ", beads.Select(b => FeatureLabel(b.ExistingFeature!)))} — select one bead's curves",
        };

        // With an error the chain is neither new nor an update, so it carries no bead to update.
        if (error is not null)
            return new CurveState(chain, fromSketch, new CurveTraceback(null, new BeadTracebackResult(false, false, null, null, null)), null, null, error);

        return new CurveState(chain, fromSketch, beads.Count == 1 ? beads[0] : tracebacks[0], null, null);
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

            // The radio has to move first: the BeadOptions tree only holds one bead SPEC's rows, so checking the
            // SPEC before the workbook is loaded would check nothing.
            if (ResolveBeadSpec(knownSpecs[0].StandardId, specId, beadSpec) is { } resolved
                && !string.Equals(resolved, _blocks.GetSelectedBeadSpec(), StringComparison.OrdinalIgnoreCase))
            {
                _blocks.SelectBeadSpec(resolved);
                LoadSelectedBeadSpec();
                RepopulateSpecVariants();
            }

            if (_currentSpecs.FirstOrDefault(s => s.SpecId == specId) is { } specRow)
            {
                _pickedSpec = specRow;
                _blocks.SetCheckedSpecRow(_pickedSpec);
            }
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
        var labels = UniqueLabels(_perCurve.Select(s => (s.BuildTarget, TargetLabel(s))).ToList());
        var lines = new List<string>();
        for (var i = 0; i < _perCurve.Count; i++)
        {
            var state = _perCurve[i];
            var label = labels[i];
            var result = state.Traceback.Result;

            var status = state switch
            {
                { ChainError: { } error } => $"ERROR: {error}",
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

    /// <summary>Names what the builder works on: the Bead feature for an existing bead, else the chain.</summary>
    private static string TargetLabel(CurveState state) =>
        state.Traceback.ExistingFeature is { } feature
            ? FeatureLabel(feature) + (state.Traceback.Result.IsPatternInstance ? " (via pattern)" : "")
            : ChainLabel(state.Chain, state.FromSketch);

    /// <summary>"Line 12", or "Line 12 +3 curve(s)" for a longer chain, plus the sketch the chain came from.</summary>
    private static string ChainLabel(IReadOnlyList<NXObject> chain, Sketch? fromSketch)
    {
        var first = chain[0];
        var label = string.IsNullOrEmpty(first.Name) ? $"{first.GetType().Name} {first.JournalIdentifier}" : first.Name;
        if (chain.Count > 1)
            label += $" +{chain.Count - 1} curve(s)";

        return fromSketch is null
            ? label
            : $"{label} (from {(string.IsNullOrEmpty(fromSketch.Name) ? fromSketch.JournalIdentifier : fromSketch.Name)})";
    }

    private static string FeatureLabel(Feature feature)
    {
        try
        {
            return feature.GetFeatureName();
        }
        catch (NXException)
        {
            return feature.JournalIdentifier;
        }
    }

    /// <summary>The labels, in order, with every one that two different targets share made distinct by the
    /// target's journal identifier — the list must never show two identical lines.</summary>
    private static List<string> UniqueLabels(IReadOnlyList<(NXObject Target, string Label)> items)
    {
        var shared = items.GroupBy(i => i.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return items
            .Select(i => shared.Contains(i.Label) ? $"{i.Label} [{i.Target.JournalIdentifier}]" : i.Label)
            .ToList();
    }

    private static string Describe(NXObject obj) => $"{obj.GetType().Name} {obj.JournalIdentifier}";

    /// <summary>Debug trace, always on for now — see the class doc. Bypasses <see cref="Render"/>'s de-duplication:
    /// a trace line is about this call, not the dialog's state.</summary>
    private void Trace(string message) => _context.Log.Info($"[TRACE] {message}");

    // ---- Sheet metal material ----

    /// <summary>Called from <c>update_cb</c> for the Standard picker.</summary>
    public void OnStandardChanged() => Guard("Standard", () =>
    {
        LoadSelectedStandard();
        RefreshMaterialFilter();
        RefreshMaterialTree();
        LoadSelectedBeadSpec();
        RepopulateSpecVariants();
        Render();
    });

    /// <summary>Called from <c>update_cb</c> for the material filter. Narrowing the tree drops the picked row
    /// whenever it is no longer one of the rows on show — a checkbox the user cannot see must not still be the
    /// thing Apply builds to.</summary>
    public void OnMaterialFilterChanged() => Guard("material filter", () =>
    {
        if (_pickedRow is { } picked && !string.Equals(picked.PhysicalMaterialName, _blocks.GetSelectedMaterialFilter(), StringComparison.OrdinalIgnoreCase))
            _pickedRow = null;

        RefreshMaterialTree();
        RepopulateSpecVariants();
        Render();
    });

    /// <summary>Called from the ShtMetal tree via <see cref="IBeadTreeSink"/> when a row's checkbox is clicked.
    /// The check is radio-like: exactly one row is checked once any is, so clicking the checked row re-asserts
    /// it rather than clearing it. Every row's state is written back, even on the no-op path, so the tree shows
    /// exactly the picked row whatever NX did to the clicked node.</summary>
    public void OnMaterialRowChecked(SheetMetalMaterialRow? row) => Guard("ShtMetal check", () =>
    {
        if (row is not null)
            _pickedRow = row;

        _blocks.SetCheckedMaterialRow(_pickedRow);

        if (row is null)
            return;

        RepopulateSpecVariants();
        Render();
    });

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
        CurrentThickness is { } thickness && !SheetMetalPreferenceCheck.ThicknessMatches(row.Thickness, thickness);

    private bool PhysicalMaterialDiffers(SheetMetalMaterialRow row) =>
        _outcome is { PhysicalMaterialName: { } bodyMaterial }
        && !string.Equals(bodyMaterial, row.PhysicalMaterialName, StringComparison.OrdinalIgnoreCase);

    // ---- Bead SPEC ----

    /// <summary>Called from <c>update_cb</c> for the bead SPEC radio.</summary>
    public void OnBeadSpecChanged() => Guard("bead SPEC", () =>
    {
        LoadSelectedBeadSpec();
        RepopulateSpecVariants();
        Render();
    });

    // ---- Direction and preview ----

    /// <summary>Called from <c>update_cb</c> for the direction block.</summary>
    public void OnDirectionFlipped() => Guard("direction", RefreshPreview);

    /// <summary>Called from <c>update_cb</c> for Show Preview.</summary>
    public void OnPreviewToggled() => Guard("preview", RefreshPreview);

    /// <summary>The side every bead is built to — absolute, so an existing bead is built to it too, and the same
    /// selection and SPEC always give the same bead.
    /// VERIFY on a live part: that unflipped (the arrow as placed, along the face normal) matches
    /// SectionNormalSide; swap the two here if the preview shows the opposite.</summary>
    private BeadBuilder.HeightSideOptions Side => _blocks.IsDirectionFlipped
        ? BeadBuilder.HeightSideOptions.SectionReverseNormalSide
        : BeadBuilder.HeightSideOptions.SectionNormalSide;

    /// <summary>Points the arrow at the first bead in the list, or hides it when there is none.</summary>
    private void PlaceDirectionArrow()
    {
        var first = _perCurve.FirstOrDefault(s => s.ChainError is null);
        var placed = first is not null && _resolvedBody is not null
            ? _directionProbe.Find(first.Chain, first.Traceback.ExistingFeature, _resolvedBody)
            : null;

        _blocks.SetDirection(placed?.Origin, placed?.Normal);
    }

    /// <summary>Takes any preview down, then — with Show Preview on and nothing blocking Apply — previews every
    /// bead Apply would build. A bead that cannot be previewed is logged and skipped: the rest still show.</summary>
    private void RefreshPreview()
    {
        DisposePreviews();

        if (!_blocks.IsPreviewOn || _perCurve.Count == 0)
            return;

        var (_, blocking) = CurrentModeAndErrorText();
        if (blocking is not null || SelectedSpec() is not { } spec || _pickedRow is null)
        {
            Trace($"Preview: not shown — {blocking ?? "choose a material row and a SPEC first"}");
            return;
        }

        foreach (var state in _perCurve)
        {
            var preview = _featureService.OpenPreview(state.Chain, spec, state.Traceback.ExistingFeature, Side);
            if (preview.Ok)
                _previews.Add(preview.Value!);
            else
                _context.Log.Warn($"Preview of '{TargetLabel(state)}' not shown: {preview.Message}");
        }

        Trace($"Preview: {_previews.Count} of {_perCurve.Count} bead(s) shown, {Side}.");
    }

    private void DisposePreviews()
    {
        foreach (var preview in _previews)
            preview.Dispose();

        _previews.Clear();
    }

    /// <summary>Reads the chosen bead SPEC's workbook, and why it has no SPECs when it has none.</summary>
    private void LoadSelectedBeadSpec()
    {
        _currentSpecs = Array.Empty<BeadSpecRow>();
        _noSpecsReason = null;

        if (_standard is null)
        {
            Trace($"Bead SPEC: not loaded, no Standard is selected (enum shows '{_blocks.GetSelectedStandardId() ?? "(none)"}').");
            return;
        }

        var beadSpec = _blocks.GetSelectedBeadSpec();
        Trace($"Bead SPEC: '{beadSpec ?? "(none)"}' of [{string.Join(", ", _blocks.GetBeadSpecNames())}], Standard '{_standard.Id}'");
        if (string.IsNullOrEmpty(beadSpec))
        {
            _noSpecsReason = "no bead SPEC is selected";
            return;
        }

        try
        {
            Trace($"Bead SPEC workbook: {_specCache.FindWorkbook(_standard, beadSpec!) ?? "(none)"} (folder '{_standard.BeadSpecFolder}')");
            _currentSpecs = _specCache.GetSpecs(_standard, beadSpec!);
            Trace($"Bead SPEC rows read: {_currentSpecs.Count}");
            if (_currentSpecs.Count == 0)
            {
                _noSpecsReason = _specCache.FindWorkbook(_standard, beadSpec!) is null
                    ? $"no workbook (.xlsx) in '{_standard.BeadSpecFolder}' is named for bead SPEC '{beadSpec}'"
                    : $"the bead SPEC workbook for '{beadSpec}' lists no SPECs";
            }
        }
        catch (Exception ex)
        {
            Trace($"Reading bead SPEC '{beadSpec}' threw: {ex}");
            _noSpecsReason = $"its bead SPEC workbook could not be read: {ex.Message}";
        }
    }

    /// <summary>The BeadOptions tree holds the chosen workbook's rows that validate against the current sheet metal.
    /// A SPEC a selected bead is already built to is kept on the tree even when it no longer validates — coloured
    /// as a warning — otherwise it would have nothing to check, and the reason it fails would have no way to become
    /// visible.</summary>
    private void RepopulateSpecVariants()
    {
        var profile = Profile;
        var specs = profile is not null
            ? _specFinder.FindValid(profile, _currentSpecs).ToList()
            : _currentSpecs.ToList();
        var validCount = specs.Count;

        foreach (var (_, specId, _) in KnownSpecs())
        {
            if (specs.All(s => s.SpecId != specId)
                && _currentSpecs.FirstOrDefault(s => s.SpecId == specId) is { } known)
            {
                specs.Add(known);
            }
        }

        var profileSource = profile is null ? "none (all rows listed)"
            : _outcome is not null ? $"body, t {profile.Thickness:0.####}, grade '{profile.MaterialGradeLabel}'"
            : $"Sheet Metal Preferences, t {profile.Thickness:0.####}, grade '{profile.MaterialGradeLabel}'";
        Trace($"SPEC variants: {_currentSpecs.Count} row(s) in the workbook, validated against {profileSource}: " +
              $"{validCount} valid, {specs.Count - validCount} kept for selected beads, {specs.Count} listed" +
              (specs.Count > 0 ? $" [{string.Join(", ", specs.Take(5).Select(s => s.SpecId))}{(specs.Count > 5 ? ", …" : "")}]" : ""));

        // The user's SPEC is carried across whenever it is still on the new tree — otherwise checking a different
        // material row would silently throw away a SPEC they had already chosen.
        _pickedSpec = specs.FirstOrDefault(s => BlockAccessor.Matches(s, _pickedSpec));

        var keptForBeads = new HashSet<BeadSpecRow>(specs.Skip(validCount));
        _blocks.PopulateSpecTree(specs, _pickedSpec, keptForBeads.Contains);
    }

    /// <summary>Called from the BeadOptions tree via <see cref="IBeadTreeSink"/> when a row's checkbox is clicked.
    /// Radio-like, exactly as <see cref="OnMaterialRowChecked"/>.</summary>
    public void OnBeadSpecRowChecked(BeadSpecRow? row) => Guard("BeadOptions check", () =>
    {
        if (row is not null)
            _pickedSpec = row;

        _blocks.SetCheckedSpecRow(_pickedSpec);

        if (row is null)
            return;

        Render();
    });

    private BeadSpecRow? SelectedSpec() => _pickedSpec;

    // ---- State and rendering ----

    /// <summary>What the dialog is doing, and the one thing stopping Apply, if any — in the order the user has to
    /// resolve them.</summary>
    private (string ModeText, string? ErrorText) CurrentModeAndErrorText()
    {
        if (_standard is null)
            return ("Choose a Standard.", null);

        if (_pickedRow is not { } row)
            return ("Choose a material, then check one of its rows.", null);

        if (ThicknessDiffers(row))
        {
            var against = _outcome is not null ? "this sheet metal is" : "the Sheet Metal Preferences specify";
            return (CurrentModeText(),
                $"Sheet metal material '{row.Name}' is {row.Thickness:0.####} thick, but {against} " +
                $"{CurrentThickness:0.####}. Check a material row of this thickness, or correct the body's thickness.");
        }

        if (_noSpecsReason is not null)
            return (CurrentModeText(), $"No bead SPEC is allowed under Standard '{_standard.Id}': {_noSpecsReason}.");

        if (_perCurve.FirstOrDefault(s => s.ChainError is not null) is { } broken)
            return (CurrentModeText(), $"'{TargetLabel(broken)}' cannot be one bead: {broken.ChainError}.");

        var spec = SelectedSpec();
        if (spec is null)
            return ("Choose a SPEC from the bead options.", null);

        if (Profile is not { } profile)
            return (CurrentModeText(), null);

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
        var buildable = _perCurve.Where(s => s.ChainError is null).ToList();
        var updateCount = buildable.Count(s => s.IsExistingFeature);
        var createCount = buildable.Count - updateCount;
        return updateCount switch
        {
            0 when createCount == 0 => "Select curves or a sketch, or bead feature(s), to begin.",
            0 => $"New bead(s): {createCount} chain(s) selected.",
            _ when createCount == 0 => $"Editing existing bead(s): {updateCount}.",
            _ => $"Mixed: {createCount} new, {updateCount} existing bead(s) to update.",
        };
    }

    private void Render()
    {
        var (modeText, errorText) = CurrentModeAndErrorText();
        Render(modeText, errorText);
    }

    /// <summary>Refreshes everything that follows the dialog's state — the SPEC values, the preferences label, the
    /// direction arrow and the bead preview — and writes the state to the listing window, which is where
    /// body/thickness/material/mode/error/warning surface.</summary>
    private void Render(string modeText, string? errorText)
    {
        var spec = SelectedSpec();
        _blocks.SetSpecPreview(spec?.RadiusAndRadS, spec?.Width, spec?.Height, spec?.DieRadiusP);
        _blocks.SetPreferenceSummary(PreferenceSummaryText());
        PlaceDirectionArrow();
        RefreshPreview();

        var lines = new List<string>
        {
            $"Body: {_outcome?.BodyName ?? "(not resolved yet)"}",
            $"Thickness: {(_outcome is { } o ? $"{o.Thickness:0.###}" : _partPreference is { } p ? $"{p.Thickness:0.###} (from Sheet Metal Preferences)" : "(unknown)")}",
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
        if ((_outcome?.Preference ?? _partPreference) is not { } preference)
            return null;

        var current = PreferenceMaterialText(preference);
        return _pickedRow is { } row && !preference.UsesMaterial(row.Name)
            ? $"{current} — set to '{row.Name}' on Apply"
            : current;
    }

    private static string PreferenceMaterialText(SheetMetalPartPreference preference) => preference switch
    {
        { IsMaterialTableEntry: false } => "not Material Table entry",
        { MaterialName: null } => "no material",
        { Row: null } => $"'{preference.MaterialName}' (not in the standards file)",
        _ => $"'{preference.MaterialName}'",
    };

    /// <summary>label_currentPref: what the part's Sheet Metal Preferences are now, and what the picked row will
    /// set them to on Apply when that differs.</summary>
    private string PreferenceSummaryText()
    {
        var preference = _outcome?.Preference ?? _partPreference;
        var current = preference is null
            ? "Sheet Metal Preferences: could not be read"
            : $"Sheet Metal Preferences: {PreferenceMaterialText(preference)}, t {preference.Thickness:0.####}" +
              (preference.Row is { } preferred ? $" (Standard {preferred.Standard})" : "");

        var picked = _pickedRow switch
        {
            null => "Picked: (no material row checked)",
            { } row when preference is not null && preference.UsesMaterial(row.Name) => $"Picked: '{row.Name}' — same as the preferences",
            { } row => $"Picked: '{row.Name}', t {row.Thickness:0.####} — set on Apply",
        };

        return $"{current}{Environment.NewLine}{picked}";
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

        if ((_outcome?.Preference ?? _partPreference) is { SheetMetalBodyCount: > 1 } preference
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
            _blocks.ShowError("Select curves or a sketch, or bead feature(s), first.");
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
            _blocks.ShowError("Nothing is selected to build. Select curves or a sketch for new beads, or existing bead feature(s).");
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

        // The preview's builders are open on the same features and curves: they go before anything is built, and the
        // refresh at the end (or a failed Apply's next callback) puts the preview back.
        DisposePreviews();
        var side = Side;

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
            var result = _featureService.CreateOrUpdate(state.Chain, spec, existing, side);
            if (!result.Ok)
            {
                anyFailed = true;
                _context.Log.Error($"Bead {(isUpdate ? "update" : "create")} failed for '{TargetLabel(state)}': {result.Message}");
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

        // Apply may have changed the body's material and the preferences, so the part is read again.
        ReadPart();
        OnSelectionChanged();
        return 0;
    }
}
