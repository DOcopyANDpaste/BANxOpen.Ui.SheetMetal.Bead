using BANxOpen.SheetMetal.Beads;
using BANxOpen.SheetMetal.Beads.Rules;
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

/// <summary>All dialog logic (with-block-ui.md §2): reads blocks through <see cref="BlockAccessor"/>, decides in Core,
/// writes to NX through the NxAdapters services. Interactive for the pickers; the model is only touched in
/// <see cref="OnApply"/>, inside one undo mark.
///
/// The part has one sheet metal body, read on open with its Sheet Metal Preferences (which preselect the Standard
/// and row). Each connected chain in the curve block is one bead; a chain of one existing bead's curves, or a picked
/// Bead feature, updates that bead.
///
/// Checking a material row (Standard → physical material → row) stages a Sheet Metal Preference — see
/// <see cref="PreferenceRow"/>. Staging is what the Bead tab goes by: SPECs are validated against the staged row's
/// thickness and grade, so checking a row re-filters the SPECs at once, and before anything is checked the part's own
/// preferences filter them. Nothing is written to the part until Apply, which commits the preference, the material and
/// the beads under one undo mark.
///
/// Beads already on the body, other than the ones selected, restrict the row: unless Show All is on, only rows every
/// one of their SPECs allows are listed, and a disallowed row blocks Apply. A bead with no stamp is identified from
/// its geometry.
///
/// Status goes to the listing window; <c>label_currentPref</c> shows the part's preferences, and the preferences Apply
/// will leave behind when the staged row differs. Every
/// callback runs through <see cref="Guard"/>. TODO: gate the <c>[TRACE]</c> lines once the dialog is signed off.
/// TODO(business): confirm the Standard is part-level rather than chosen per bead.</summary>
public sealed class BeadDialogPresenter : IBeadTreeSink, IDisposable
{
    /// <summary>The material filter's first member, so the tree only ever lists a material the user chose.</summary>
    private const string ChooseMaterialOption = "(choose a material)";

    private readonly NxSessionContext _context;
    private readonly BlockAccessor _blocks;
    private readonly BeadSpecCache _specCache;
    private readonly BeadSpecValidator _validator;
    private readonly BeadSpecFinder _specFinder;
    private readonly BeadSelectionExpander _selectionExpander;
    private readonly SheetMetalProfileReader _profileReader;
    private readonly BeadTracebackService _tracebackService;
    private readonly BeadFeatureService _featureService;
    private readonly SheetMetalMaterialAssignment _materialAssignment;
    private readonly IBeadSpecLookup _specLookup;
    private readonly IFeatureInventory _featureInventory;
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

    // The part's one sheet metal body and its profile, read on open and after each Apply. Null, with _bodyError
    // saying why, when the part does not have exactly one.
    private Body? _body;
    private ProfileReadOutcome? _outcome;
    private string? _bodyError;

    // Every bead on the body and the SPEC it is built to, read with the body.
    private IReadOnlyList<BeadOnBody> _partBeads = Array.Empty<BeadOnBody>();

    // The beads Apply builds or updates, one per line of the selection list. Recomputed on every selection change.
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
        BeadSelectionExpander selectionExpander,
        SheetMetalProfileReader profileReader,
        BeadTracebackService tracebackService,
        BeadFeatureService featureService,
        SheetMetalMaterialAssignment materialAssignment,
        IBeadSpecLookup specLookup,
        IFeatureInventory featureInventory,
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
        _selectionExpander = selectionExpander;
        _profileReader = profileReader;
        _tracebackService = tracebackService;
        _featureService = featureService;
        _materialAssignment = materialAssignment;
        _specLookup = specLookup;
        _featureInventory = featureInventory;
        _beadSettings = beadSettings;
        _preferences = preferences;
        _materialTable = materialTable;
        _directionProbe = directionProbe;
    }

    /// <summary>One bead to build or update: a chain of curves, or an existing Bead feature.</summary>
    /// <param name="Chain">The curves a new bead is built from; empty for a picked Bead feature.</param>
    /// <param name="Sources">What the user picked for this line (curves and/or Bead features, duplicates included), so
    /// the list's delete button takes exactly those out of the selection blocks.</param>
    /// <param name="MatchedSpec">The SPEC an unstamped bead was identified as, or null.</param>
    /// <param name="UnmatchedReason">Why an unstamped bead could not be identified, or null.</param>
    /// <param name="ChainError">Why the chain cannot be one bead. Listed, and blocks Apply.</param>
    private sealed record CurveState(
        IReadOnlyList<NXObject> Chain, Sketch? FromSketch, CurveTraceback Traceback, List<NXObject> Sources,
        BeadSpecRow? MatchedSpec = null, string? UnmatchedReason = null, string? ChainError = null)
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

        /// <summary>The Standard/SPEC this bead is built to, from its stamp or its geometry. <c>BeadSpec</c> is null
        /// for a bead stamped before the bead SPEC name was recorded.</summary>
        public (string StandardId, string SpecId, string? BeadSpec)? KnownSpec =>
            Traceback.Result is { Found: true, StandardId: { } standardId, SpecId: { } specId }
                ? (standardId, specId, Traceback.Result.BeadSpec)
                : MatchedSpec is { } matched ? (matched.StandardId, matched.SpecId, matched.WorkbookName) : null;
    }

    /// <summary>The material row the part's Sheet Metal Preferences hold now. Null when they are not a Material Table
    /// entry, or their material is not in the standards file — in both cases NX is not going by a row. Also null when
    /// that row belongs to another Standard than the chosen one, the same rule <see cref="LoadSelectedStandard"/>
    /// applies to a picked row: the material and the Standard must agree, or a grade would be matched against another
    /// Standard's SPEC workbooks.</summary>
    private SheetMetalMaterialRow? PartPreferenceRow =>
        (_outcome?.Preference ?? _partPreference) is { IsMaterialTableEntry: true, Row: { } row } && IsCurrentStandard(row.Standard)
            ? row
            : null;

    /// <summary>The row the preferences stand for as the dialog has them staged: the checked row once one is checked,
    /// else what the part holds. Nothing is written until Apply, so this is the dialog's own view of the preference —
    /// and it is exactly the row Apply writes. Everything the Bead tab judges a SPEC by comes from here, so checking a
    /// row re-filters BeadOptions at once, and before anything is checked the part's own preference filters it.</summary>
    private SheetMetalMaterialRow? PreferenceRow => _pickedRow ?? PartPreferenceRow;

    /// <summary>The thickness SPECs and rows are judged against. The staged preference row's first: NX resets the sheet
    /// metal thickness from the Material Table row when the preferences commit, so a staged row of a different thickness
    /// is a thickness change NX will make, not a mismatch. The body's own, then the preferences', when no row is
    /// staged.</summary>
    private double? CurrentThickness => PreferenceRow?.Thickness ?? _outcome?.Thickness ?? _partPreference?.Thickness;

    /// <summary>What a SPEC is validated against: the staged preference's thickness and grade. Null when neither the
    /// part nor the user has named a row.</summary>
    private SheetMetalProfile? Profile =>
        PreferenceRow is { } row && CurrentThickness is { } thickness ? new SheetMetalProfile(thickness, row.Grade) : null;

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
        LoadSelectedBeadSpec();

        // The part first: its beads decide which material rows the filter offers.
        ReadPart();
        PreselectFromPreferences();
        RefreshMaterialFilter();
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

    /// <summary>Reads the part: its Sheet Metal Preferences, its one sheet metal body (checked against them), and the
    /// beads on that body. A part without exactly one sheet metal body sets <see cref="_bodyError"/>, which blocks
    /// Apply; everything else wrong only goes to the listing window.</summary>
    private void ReadPart()
    {
        var read = _preferences.ReadForPart();
        _partPreference = read.Preference;

        if (_partPreference is { } preference)
        {
            Trace($"Sheet Metal Preferences: material '{preference.MaterialName ?? "(none)"}', " +
                  $"Material Table entry {preference.IsMaterialTableEntry}, thickness {preference.Thickness:0.####}, " +
                  $"row {(preference.Row is { } row ? $"'{row.Name}' (Standard {row.Standard})" : "not in the standards file")}");
        }
        else
        {
            _context.Log.Warn($"Could not read this part's Sheet Metal Preferences: {read.ReadError ?? "no preferences were returned"}.");
        }

        _body = null;
        _outcome = null;
        _partBeads = Array.Empty<BeadOnBody>();

        var bodies = SheetMetalBodies();
        if (bodies.Count != 1)
        {
            _bodyError = $"This part has {bodies.Count} sheet metal bodies; the Bead tool needs exactly one.";
            _context.Log.Error(_bodyError);
            return;
        }

        var profile = _profileReader.ReadFor(bodies[0]);
        if (!profile.Ok)
        {
            _bodyError = profile.Message ?? $"Could not read the sheet metal of body '{bodies[0].Name}'.";
            _context.Log.Error(_bodyError);
            return;
        }

        _bodyError = null;
        _body = bodies[0];
        _outcome = profile.Value!;
        Trace($"Body: '{_outcome.BodyName}', thickness {_outcome.Thickness:0.####}, material '{_outcome.PhysicalMaterialName ?? "(none)"}'");

        if (_partPreference is { } partPreference)
        {
            var check = SheetMetalPreferenceCheck.Evaluate(_outcome.PhysicalMaterialName, _outcome.Thickness, partPreference);
            if (check.Status != SheetMetalPreferenceStatus.InSync)
                _context.Log.Warn($"Body '{_outcome.BodyName}' does not match the Sheet Metal Preferences: {check.Message}");
        }

        var inventory = _featureInventory.Read(_outcome.BodyId);
        if (inventory.ReadError is { } error)
            _context.Log.Warn($"The beads on this body could not be read, so they do not restrict the material: {error}");
        else
            _partBeads = BeadsOnBody.Resolve(inventory, _specLookup, _beadSettings);

        Trace($"Beads on the body: {_partBeads.Count} [{string.Join(", ", _partBeads.Select(b => $"{b.Name}: {b.SpecId ?? b.Status.ToString()}"))}]");
    }

    private List<Body> SheetMetalBodies()
    {
        try
        {
            var manager = _context.WorkPart.Features.SheetmetalManager;
            return _context.WorkPart.Bodies.Cast<Body>().Where(b => IsSheetMetal(manager, b)).ToList();
        }
        catch (NXException ex)
        {
            _context.Log.Error($"Could not list the part's bodies: NX {ex.ErrorCode}: {ex.Message}");
            return new List<Body>();
        }

        static bool IsSheetMetal(SheetmetalManager manager, Body body)
        {
            try
            {
                return manager.IsSheetmetalBody(body);
            }
            catch (NXException)
            {
                return false;
            }
        }
    }

    /// <summary>The beads on the body that restrict the material: all but the ones selected, which Apply rebuilds to
    /// the chosen SPEC.</summary>
    private IEnumerable<BeadOnBody> ConstrainingBeads()
    {
        var selected = new HashSet<string>(
            _perCurve.Select(s => s.Traceback.ExistingFeature?.Tag.ToString()).Where(k => k is not null)!);
        return _partBeads.Where(b => !selected.Contains(b.FeatureKey));
    }

    /// <summary>Why the beads already on the body refuse a material row. Null from <see cref="BeadRefusalFor"/> when
    /// they allow it; otherwise at least one list is non-empty.</summary>
    /// <param name="ThicknessNotCovered">Beads whose SPEC is not driven for the row's thickness at all, so they
    /// could not be rebuilt into a sheet of it.</param>
    /// <param name="GradeNotAllowed">Beads whose SPEC is driven for the row's thickness but does not allow its
    /// grade there.</param>
    private sealed record BeadRefusal(
        IReadOnlyList<BeadOnBody> ThicknessNotCovered,
        IReadOnlyList<BeadOnBody> GradeNotAllowed);

    /// <summary>Why the beads already on this body refuse <paramref name="row"/>, or null when they allow it.
    ///
    /// Judged at the ROW's own thickness, not at one "current" thickness: Apply re-thicknesses the sheet from the
    /// Material Table row and NX rebuilds every bead into it, so the SPEC row that applies to a bead is its family's
    /// row for that thickness. A SPEC id names a family — one row per thickness, each with its own allowed grades —
    /// which is why a single row could never answer this (see <see cref="BeadOnBody.SpecAt"/>).
    ///
    /// A bead whose SPEC is not known cannot restrict anything; it is reported in <see cref="Warnings"/> instead.
    /// No constraining beads at all means unrestricted.</summary>
    private BeadRefusal? BeadRefusalFor(SheetMetalMaterialRow row)
    {
        List<BeadOnBody>? thicknessNotCovered = null;
        List<BeadOnBody>? gradeNotAllowed = null;

        foreach (var bead in ConstrainingBeads())
        {
            if (!bead.IsSpecKnown)
                continue;

            if (bead.SpecAt(row.Thickness) is not { } specRow)
                (thicknessNotCovered ??= new List<BeadOnBody>()).Add(bead);
            else if (!specRow.IsAllowedFor(row.Grade))
                (gradeNotAllowed ??= new List<BeadOnBody>()).Add(bead);
        }

        return thicknessNotCovered is null && gradeNotAllowed is null
            ? null
            : new BeadRefusal(
                (IReadOnlyList<BeadOnBody>?)thicknessNotCovered ?? Array.Empty<BeadOnBody>(),
                (IReadOnlyList<BeadOnBody>?)gradeNotAllowed ?? Array.Empty<BeadOnBody>());
    }

    private bool GradeAllowed(SheetMetalMaterialRow row) => BeadRefusalFor(row) is null;

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

        // The caller refreshes the filter next, which moves it to the picked row's material — so the preferred row is
        // on show, and checked, without a second write that would look like a user action.
        _pickedRow = row;
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
        var deleted = new HashSet<int>(indices.Where(i => i >= 0 && i < _perCurve.Count));
        if (deleted.Count == 0)
            return;

        var remaining = _perCurve.Where((_, i) => !deleted.Contains(i)).Select(s => s.Sources).ToList();
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

        TraceAllCurves(expanded.Items);

        // The selected beads no longer restrict the material, so the material pickers follow the selection.
        RefreshMaterialFilter();
        RefreshMaterialTree();
        RepopulateSpecVariants();
        PlaceDirectionArrow();
        UpdateModeAndSpecPickers();
    }

    /// <summary>What one expanded item was picked as: its curves, or the Bead feature itself.</summary>
    private static List<NXObject> SourcesOf(ExpandedSelectionItem item) =>
        item.BeadFeature is { } feature ? new List<NXObject> { feature } : item.Curves.ToList();

    private void TraceAllCurves(IReadOnlyList<ExpandedSelectionItem> selection)
    {
        _perCurve.Clear();

        // One entry per build target: a Bead feature picked directly and a chain of its own section curves, or two
        // pattern members of one stamped original, all come down to one thing for the builder to work on.
        // Build target key -> its line, so a duplicate's objects are deleted along with the line it folded into.
        var targets = new Dictionary<string, int>();

        foreach (var item in selection)
        {
            var state = item.BeadFeature is { } picked
                ? new CurveState(Array.Empty<NXObject>(), null, _tracebackService.Trace(picked), SourcesOf(item))
                : TraceChain(item.Curves, item.FromSketch, SourcesOf(item));

            if (targets.TryGetValue(state.BuildTargetKey, out var line))
            {
                Trace($"Left out (already listed): {TargetLabel(state)}");
                _perCurve[line].Sources.AddRange(state.Sources);
                continue;
            }

            targets[state.BuildTargetKey] = _perCurve.Count;

            // An unstamped bead was identified from its geometry when the part was read.
            if (state.IsUnstamped && state.Traceback.ExistingFeature is { } feature)
            {
                var onBody = _partBeads.FirstOrDefault(b => b.FeatureKey == feature.Tag.ToString());
                state = state with
                {
                    // Any row of the family: KnownSpec reads only the Standard, SPEC id and workbook off it, and
                    // every row under one SPEC id shares all three.
                    MatchedSpec = onBody?.SpecFamily.FirstOrDefault(),
                    UnmatchedReason = onBody is null ? "it is not among the beads read from this part's body" : onBody.UnidentifiedReason,
                };
            }

            _perCurve.Add(state);
        }

        Trace($"Build targets: {_perCurve.Count} ({_perCurve.Count(s => s.IsExistingFeature)} existing bead(s), " +
              $"{_perCurve.Count(s => s.ChainError is not null)} with an error).");
    }

    /// <summary>A chain is one bead. When none of its curves belongs to a bead it is a new one; when all of them
    /// belong to the same bead, that bead is updated. Anything else — curves of several beads, or a bead's curves
    /// mixed with new ones — cannot be one bead, and says so.</summary>
    private CurveState TraceChain(IReadOnlyList<NXObject> chain, Sketch? fromSketch, List<NXObject> sources)
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
            return new CurveState(chain, fromSketch, new CurveTraceback(null, new BeadTracebackResult(false, false, null, null, null)), sources, ChainError: error);

        return new CurveState(chain, fromSketch, beads.Count == 1 ? beads[0] : tracebacks[0], sources);
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

            // The variant driven for the staged thickness, not merely the first row carrying the SPEC id: the
            // workbook holds one row per thickness under that id, and checking the wrong one would fail validation
            // on a bead that is in fact correct.
            if (SpecVariantAt(specId) is { } specRow)
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

    /// <summary>Which bead SPEC a known bead belongs to: from its stamp, or for an older stamp by its SPEC id. Null when
    /// that cannot be pinned down; the radio is then left alone rather than pointed at a guess.</summary>
    private string? ResolveBeadSpec(string standardId, string specId, string? stamped)
    {
        if (!string.IsNullOrEmpty(stamped))
            return MatchBeadSpecName(stamped!);

        // Any row of the family answers this: a family lives in one workbook, or FindAll refuses to resolve it.
        return _specLookup.FindAll(standardId, specId).FirstOrDefault() is { } row
            ? MatchBeadSpecName(row.WorkbookName)
            : null;
    }

    /// <summary>The dialog's bead SPEC whose name the workbook name contains — the rule the workbook was found by. The
    /// longest wins, so a SPEC name inside another cannot shadow it.</summary>
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

    /// <summary>Whether <paramref name="block"/> is Show All, for <c>update_cb</c>.</summary>
    public bool IsShowAllToggle(NXOpen.BlockStyler.UIBlock block) => _blocks.IsShowAllToggle(block);

    /// <summary>Called from <c>update_cb</c> for Show All: lists every row of the Standard, or only the ones the beads
    /// on the body allow.</summary>
    public void OnShowAllToggled() => Guard("Show All", () =>
    {
        RefreshMaterialFilter();
        RefreshMaterialTree();
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

    /// <summary>The physical materials of the listed rows, behind a "choose" prompt. The current choice is kept while it
    /// is still offered.</summary>
    private void RefreshMaterialFilter()
    {
        var materials = ListedRows()
            .Select(r => r.PhysicalMaterialName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = _pickedRow?.PhysicalMaterialName ?? _blocks.GetSelectedMaterialFilter();
        _blocks.PopulateMaterialFilter(new[] { ChooseMaterialOption }.Concat(materials).ToList());
        _blocks.SelectMaterialFilter(
            materials.FirstOrDefault(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase)) ?? ChooseMaterialOption);
    }

    /// <summary>Lists the chosen material's rows. A row whose thickness differs from the body, or whose grade the beads
    /// on the body do not allow, is coloured, and picking it is reported rather than prevented.</summary>
    private void RefreshMaterialTree()
    {
        var filter = _blocks.GetSelectedMaterialFilter();
        IReadOnlyList<SheetMetalMaterialRow> rows = filter is null or ChooseMaterialOption
            ? Array.Empty<SheetMetalMaterialRow>()
            : ListedRows()
                .Where(r => string.Equals(r.PhysicalMaterialName, filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        _blocks.PopulateMaterialTree(rows, _pickedRow, row => ThicknessDiffers(row) || !GradeAllowed(row));
    }

    /// <summary>The chosen Standard's rows on offer: with Show All off, only those the beads on the body allow. The
    /// picked row stays listed either way, so the preselected row can still be seen — and why it blocks.</summary>
    private IEnumerable<SheetMetalMaterialRow> ListedRows() =>
        _blocks.IsShowAllOn
            ? CurrentStandardRows()
            : CurrentStandardRows().Where(r => GradeAllowed(r) || BlockAccessor.Matches(r, PreferenceRow));

    private IReadOnlyList<SheetMetalMaterialRow> CurrentStandardRows() =>
        _standard is null ? Array.Empty<SheetMetalMaterialRow>() : _materialTable.RowsFor(_standard.Id);

    /// <summary>Whether picking <paramref name="row"/> changes the sheet thickness. Against the BODY's thickness, not
    /// <see cref="CurrentThickness"/>, which follows the staged row and would compare it against itself. This does not
    /// block: NX re-thicknesses the sheet from the row when the preferences commit. It colours the row in the tree and
    /// raises an advisory, because re-thicknessing a part that already has geometry is worth seeing first.</summary>
    private bool ThicknessDiffers(SheetMetalMaterialRow row) =>
        _outcome?.Thickness is { } bodyThickness && !SheetMetalPreferenceCheck.ThicknessMatches(row.Thickness, bodyThickness);

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
    public void OnDirectionFlipped() => Guard("direction", () => RefreshPreview(CurrentModeAndErrorText().ErrorText));

    /// <summary>Called from <c>update_cb</c> for Show Preview.</summary>
    public void OnPreviewToggled() => Guard("preview", () => RefreshPreview(CurrentModeAndErrorText().ErrorText));

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
        var placed = first is not null && _body is not null
            ? _directionProbe.Find(first.Chain, first.Traceback.ExistingFeature, _body)
            : null;

        _blocks.SetDirection(placed?.Origin, placed?.Normal);
    }

    /// <summary>Takes any preview down, then — with Show Preview on and nothing blocking Apply — previews every
    /// bead Apply would build. A bead that cannot be previewed is logged and skipped: the rest still show.</summary>
    private void RefreshPreview(string? blocking)
    {
        DisposePreviews();

        if (!_blocks.IsPreviewOn || _perCurve.Count == 0)
            return;

        if (blocking is not null || _pickedSpec is not { } spec || PreferenceRow is null)
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
    /// <summary>The loaded workbook's row for <paramref name="specId"/> driven for the staged thickness.
    ///
    /// A SPEC id is carried by one row per thickness, so "the row for this SPEC" is only a question once a thickness
    /// is in hand. Null when none is, or when the SPEC is not driven for it — which is a real answer: there is no
    /// variant of that SPEC this sheet could be given, and offering the wrong one would only fail validation.</summary>
    private BeadSpecRow? SpecVariantAt(string specId) =>
        CurrentThickness is { } thickness
            ? _currentSpecs.FirstOrDefault(s =>
                string.Equals(s.SpecId, specId, StringComparison.OrdinalIgnoreCase)
                && ThicknessMatchRule.Matches(s.Thickness, thickness))
            : null;

    private void RepopulateSpecVariants()
    {
        var profile = Profile;
        var specs = profile is not null
            ? _specFinder.FindValid(profile, _currentSpecs).ToList()
            : _currentSpecs.ToList();
        var validCount = specs.Count;

        foreach (var (_, specId, _) in KnownSpecs())
        {
            if (specs.All(s => s.SpecId != specId) && SpecVariantAt(specId) is { } known)
                specs.Add(known);
        }

        var profileSource = profile is null ? "none (all rows listed)"
            : $"{(_pickedRow is not null ? "the staged row" : "the part's Sheet Metal Preferences")}, " +
              $"t {profile.Thickness:0.####}, grade '{profile.MaterialGradeLabel}'";
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

    // ---- State and rendering ----

    /// <summary>What the dialog is doing, and the one thing stopping Apply, if any — in the order the user has to
    /// resolve them.</summary>
    private (string ModeText, string? ErrorText) CurrentModeAndErrorText()
    {
        if (_bodyError is not null)
            return (CurrentModeText(), _bodyError);

        if (_standard is null)
            return ("Choose a Standard.", null);

        // The part's own preference row counts: only a part whose preferences name no standards-file row needs a pick
        // before anything can be judged.
        if (PreferenceRow is not { } row)
            return ("Choose a material, then check one of its rows.", null);

        // A row of a different thickness than the BODY's does NOT block — NX re-thicknesses the sheet from it. See
        // ThicknessDiffers; it is reported through Warnings() instead. A thickness the beads' own SPECs are not
        // driven for is a different matter, and does block — that is the check below.

        if (BeadRefusalFor(row) is { } refusal)
            return (CurrentModeText(), BeadRefusalText(row, refusal));

        if (_noSpecsReason is not null)
            return (CurrentModeText(), $"No bead SPEC is allowed under Standard '{_standard.Id}': {_noSpecsReason}.");

        if (_perCurve.FirstOrDefault(s => s.ChainError is not null) is { } broken)
            return (CurrentModeText(), $"'{TargetLabel(broken)}' cannot be one bead: {broken.ChainError}.");

        if (_pickedSpec is not { } spec)
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

    /// <summary>Why the beads on the body refuse this row, naming them. The thickness is stated because the answer
    /// depends on it: a SPEC allows a grade at some of the thicknesses it is driven for and not others.
    ///
    /// A SPEC not driven for the row's thickness at all is reported first and on its own — it is the coarser
    /// problem, and "which grades it allows" has no meaning at a thickness it does not cover.</summary>
    private string BeadRefusalText(SheetMetalMaterialRow row, BeadRefusal refusal)
    {
        if (refusal.ThicknessNotCovered.Count > 0)
        {
            var uncovered = refusal.ThicknessNotCovered
                .Select(b => $"SPEC {b.SpecId} (bead '{b.Name}') covers {b.ThicknessCoverage}");
            return $"Sheet metal material '{row.Name}' would make this sheet {row.Thickness:0.####} in thick, which the " +
                   $"bead(s) already on this body are not driven for: {string.Join(", ", uncovered)}. They cannot be " +
                   "rebuilt at that thickness. Check a row at a thickness they cover, or select those beads so Apply " +
                   "rebuilds them to the chosen SPEC.";
        }

        var refusing = refusal.GradeNotAllowed.Select(b => $"'{b.Name}' (SPEC {b.SpecId})");
        return $"Sheet metal material '{row.Name}' (grade '{row.Grade}') is not allowed at {row.Thickness:0.####} in by " +
               $"the bead(s) already on this body: {string.Join(", ", refusing)}. Check a row they allow, or select " +
               "those beads so Apply rebuilds them to the chosen SPEC.";
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

    /// <summary>What the beads already on the body allow at the staged thickness, for the listing window. Null when
    /// nothing restricts the grade, so the line is left out rather than saying "anything".
    ///
    /// At the staged thickness specifically: every listed row is judged at its OWN thickness
    /// (<see cref="BeadRefusalFor"/>), so this is a readout of the current state, not the whole rule.</summary>
    private string? BeadsAllowText()
    {
        if (CurrentThickness is not { } thickness)
            return null;

        var beads = ConstrainingBeads().Where(b => b.IsSpecKnown).ToList();
        if (beads.Count == 0)
            return null;

        var uncovered = beads.Where(b => b.SpecAt(thickness) is null).ToList();
        if (uncovered.Count > 0)
        {
            return $"nothing at {thickness:0.####} in — " +
                   string.Join(", ", uncovered.Select(b => $"SPEC {b.SpecId} covers {b.ThicknessCoverage}"));
        }

        var grades = BeadSpecFinder.GradesAllowedByAll(beads.Select(b => b.SpecAt(thickness)!), thickness);
        return grades is null || grades.Count == 0
            ? $"nothing at {thickness:0.####} in"
            : $"{string.Join(", ", grades.OrderBy(g => g, StringComparer.Ordinal))} at {thickness:0.####} in " +
              $"(from {beads.Count} bead(s): {string.Join(", ", beads.Select(b => b.SpecId).Distinct())})";
    }

    /// <summary>Refreshes everything that follows the dialog's state — the SPEC values, the preferences label and the
    /// bead preview — and writes the state to the listing window, which is where
    /// body/thickness/material/mode/error/warning surface.</summary>
    private void Render(string modeText, string? errorText)
    {
        var spec = _pickedSpec;
        _blocks.SetSpecPreview(spec?.RadiusAndRadS, spec?.Width, spec?.Height, spec?.DieRadiusP);
        _blocks.SetPreferenceSummary(PreferenceSummaryText());
        RefreshPreview(errorText);

        var lines = new List<string>
        {
            $"Body: {_outcome?.BodyName ?? "(none)"}",
            $"Thickness: {ThicknessText()}",
        };

        if (_outcome is not null)
            lines.Add($"Material: {_outcome?.PhysicalMaterialName ?? "(none — applied from the sheet metal material)"}");

        if (SheetMetalMaterialText() is { } materialText)
            lines.Add($"Sheet Metal Material: {materialText}");

        if (PreferencesText() is { } preferencesText)
            lines.Add($"Preferences: {preferencesText}");

        if (BeadsAllowText() is { } beadsAllowText)
            lines.Add($"Beads on this body allow: {beadsAllowText}");

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

    /// <summary>The body's thickness, and the staged row's as well when Apply would change it to that.</summary>
    private string ThicknessText()
    {
        var body = _outcome is { } o
            ? $"{o.Thickness:0.###}"
            : _partPreference is { } p ? $"{p.Thickness:0.###} (from Sheet Metal Preferences)" : "(unknown)";

        return _pickedRow is { } staged && ThicknessDiffers(staged)
            ? $"{body} — {staged.Thickness:0.###} on Apply, from '{staged.Name}'"
            : body;
    }

    private string? SheetMetalMaterialText()
    {
        if (PreferenceRow is not { } row)
            return _standard is null ? "(choose a Standard)" : "(not chosen)";

        var text = $"{row.Name} — {row.PhysicalMaterialName}, grade {row.Grade}";
        if (_pickedRow is null)
            text += " (from the part's Sheet Metal Preferences)";

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
            ? $"{current} — '{row.Name}' commits on Apply"
            : current;
    }

    private static string PreferenceMaterialText(SheetMetalPartPreference preference) => preference switch
    {
        { IsMaterialTableEntry: false } => "not Material Table entry",
        { MaterialName: null } => "no material",
        { Row: null } => $"'{preference.MaterialName}' (not in the standards file)",
        _ => $"'{preference.MaterialName}'",
    };

    /// <summary>label_currentPref: the part's Sheet Metal Preferences as they stand, and — only when the staged row
    /// would change them — the preferences as they will stand after Apply. Both lines are the preferences themselves,
    /// not the tree's checkbox: the checked row is how the user stages a preference, and once staged it is what the
    /// dialog goes by, so restating it as a separate "picked" thing would say the same thing twice.</summary>
    private string PreferenceSummaryText()
    {
        var preference = _outcome?.Preference ?? _partPreference;
        if (preference is null)
            return "Sheet Metal Preferences: could not be read";

        var current = $"Sheet Metal Preferences: {PreferenceMaterialText(preference)}, t {preference.Thickness:0.####}" +
                      (preference.Row is { } preferred ? $", grade {preferred.Grade} (Standard {preferred.Standard})" : "");

        // Only a pick can differ from what the part holds, so there is a second line only when one is checked and the
        // preferences are not already on it.
        if (_pickedRow is not { } staged || preference.UsesMaterial(staged.Name))
            return current;

        return $"{current}{Environment.NewLine}" +
               $"Updated to: '{staged.Name}', t {staged.Thickness:0.####}, grade {staged.Grade} — commits on Apply";
    }

    /// <summary>Advisories that do not stop Apply.</summary>
    private string? Warnings()
    {
        var warnings = new List<string>();

        // NX re-thicknesses the sheet from the row, so this is not a block — but it rebuilds every feature on the body,
        // so it should not happen unnoticed.
        if (_pickedRow is { } staged && ThicknessDiffers(staged) && _outcome is { } body)
        {
            warnings.Add(
                $"Sheet metal material '{staged.Name}' is {staged.Thickness:0.####} thick, so Apply changes this " +
                $"sheet's thickness from {body.Thickness:0.####} to {staged.Thickness:0.####}. Every feature on the " +
                "body is rebuilt to it, and the SPECs listed are the ones valid at the new thickness.");
        }

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

        var unknown = ConstrainingBeads().Where(b => !b.IsSpecKnown).Select(b => $"'{b.Name}'").ToList();
        if (unknown.Count > 0)
        {
            warnings.Add(
                $"The SPEC of bead(s) {string.Join(", ", unknown)} on this body is not known, so they do not restrict the " +
                "material. Select them and Apply to record a SPEC.");
        }

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    // ---- Apply ----

    public int OnApply()
    {
        // Covers the part (exactly one sheet metal body), the row, the beads already on the body, and the SPEC.
        var (_, blocking) = CurrentModeAndErrorText();
        if (blocking is not null)
        {
            _blocks.ShowError(blocking);
            return 1;
        }

        // PreferenceRow, not _pickedRow: when the part's own preferences already name the row, there is nothing to pick
        // and nothing below writes anything — Apply just builds the beads.
        if (_outcome is not { } outcome || PreferenceRow is not { } row || _pickedSpec is not { } spec)
        {
            _blocks.ShowError("Choose a material row and a SPEC first.");
            return 1;
        }

        if (_perCurve.Count == 0)
        {
            _blocks.ShowError("Nothing is selected to build. Select curves or a sketch for new beads, or existing bead feature(s).");
            return 1;
        }

        // Replacing a different material is confirmed by the engine's own reassignment rule, inside Assign below.
        LibraryMaterial? material = null;
        if (outcome.PhysicalMaterialName is null || PhysicalMaterialDiffers(row))
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

        // One undo mark: returning without Commit undoes every bead built, the material and the preferences.
        using var undo = new UndoScope(_context.Session, "Create/Update Bead", _context.Log.Error);

        // Beads first, stamped with the new SPEC: the material engine's bead constraint then checks the material
        // against the SPECs the selected beads are about to carry, not the ones they are being rebuilt from.
        if (!BuildBeads(spec))
        {
            _blocks.ShowError("One or more beads failed — see the listing window for details. No changes were committed.");
            return 1;
        }

        var warnings = new List<string>();
        if (material is not null)
        {
            var assigned = _materialAssignment.Assign(outcome.BodyId, material, row, _blocks.Confirm);
            if (!assigned.Ok)
            {
                // Declining the engine's own confirmation is the user's choice, not an error to report back to them.
                if (assigned.ErrorCode != "ASSIGNMENT_DECLINED")
                    _blocks.ShowError(assigned.Message ?? "Material assignment failed. No changes were made.");
                return 1;
            }

            warnings.AddRange(assigned.Value ?? Array.Empty<string>());
        }
        else if (!outcome.Preference.UsesMaterial(row.Name))
        {
            // Assign sets the preferences itself; an unchanged material leaves them to set here.
            var synced = _preferences.SyncMaterial(row);
            if (!synced.Ok)
            {
                _blocks.ShowError($"{synced.Message ?? "Sheet Metal Preferences could not be updated."} No changes were made.");
                return 1;
            }
        }

        undo.Commit();

        var message = "Bead(s) created/updated.";
        if (warnings.Count > 0)
            message += $"{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, warnings)}";

        _blocks.ShowResult(OperationResult.Success(), message);

        // Apply may have changed the body's material, the preferences and its beads, so the part is read again.
        ReadPart();
        OnSelectionChanged();
        return 0;
    }

    /// <summary>Builds or updates every listed bead to <paramref name="spec"/> and stamps it. False when any failed; each
    /// failure is logged, and the caller's undo mark takes back the ones that succeeded.</summary>
    private bool BuildBeads(BeadSpecRow spec)
    {
        // The bead SPEC recorded on the bead is the one the dialog offers, not the workbook's file name, so a
        // file rename or a revision suffix cannot orphan a stamped bead.
        var beadSpec = MatchBeadSpecName(spec.WorkbookName) ?? spec.WorkbookName;
        var side = Side;
        var allBuilt = true;

        foreach (var state in _perCurve)
        {
            var existing = state.Traceback.ExistingFeature;
            var result = _featureService.CreateOrUpdate(state.Chain, spec, existing, side);
            if (!result.Ok)
            {
                allBuilt = false;
                _context.Log.Error($"Bead {(existing is null ? "create" : "update")} failed for '{TargetLabel(state)}': {result.Message}");
                continue;
            }

            // Named before it is stamped: the namer reads the previous SPEC id off the feature to strip the prefix it
            // wrote last time, and Stamp overwrites that attribute.
            BeadFeatureNamer.TryApplySpecName(result.Value!, spec.SpecId, _context.Log.Warn);
            BeadAttributeWriter.Stamp(result.Value!, spec.StandardId, beadSpec, spec.SpecId, isNewFeature: existing is null);
        }

        return allBuilt;
    }
}
