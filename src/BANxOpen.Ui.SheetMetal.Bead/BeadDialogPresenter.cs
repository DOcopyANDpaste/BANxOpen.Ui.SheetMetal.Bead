using BANxOpen.SheetMetal.Beads;
using BANxOpen.SheetMetal.Materials;
using BANxOpen.SheetMetal.SpecData;
using NXOpen;
using NXOpen.Features;
using BANxOpen.SheetMetal.NxAdapters.Beads;
using BANxOpen.SheetMetal.NxAdapters.Materials;
using BANxOpen.SheetMetal.NxAdapters.Common;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.SheetMetal.Common;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>All dialog logic, per with-block-ui.md §2 — reads dialog values via <see cref="BlockAccessor"/>,
/// hands plain values to Core, applies results via the NxAdapters services. <c>BLOCKUI_BEAD.cs</c> (hand-
/// edited per its own banner comments) delegates every callback straight into this class's public methods.
///
/// Bead creation itself is modal one-shot (logic only in <see cref="OnApply"/>, per with-block-ui.md §5);
/// everything else (<see cref="OnSelectionChanged"/>, <see cref="OnStandardChanged"/>,
/// <see cref="OnSpecChanged"/>, <see cref="OnClearAllClicked"/>) reacts live so the ShtMetal tree / SPEC
/// pickers / selection-info list track the current selection as the user works.</summary>
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
    private readonly IMaterialPicker _materialPicker;
    private readonly MaterialAssigner _materialAssigner;

    private IReadOnlyList<StandardInfo> _standards = Array.Empty<StandardInfo>();
    private IReadOnlyList<BeadSpecRow> _currentStandardSpecs = Array.Empty<BeadSpecRow>();

    // Recomputed on every OnSelectionChanged.
    private Body? _resolvedBody;
    private SheetMetalProfile? _profile;
    private string? _materialDisplayName;
    private bool _materialMissing;
    private readonly List<(NXObject Curve, CurveTraceback Traceback)> _perCurveTraceback = new();
    private bool _hasBlockingUnstampedBead;

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
        IMaterialPicker materialPicker,
        MaterialAssigner materialAssigner)
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
        _materialPicker = materialPicker;
        _materialAssigner = materialAssigner;
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
            _perCurveTraceback.Clear();
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

        if (_materialMissing)
        {
            _perCurveTraceback.Clear();
            _blocks.SetSelectionInfo(Array.Empty<string>());
            RenderSheetMetalTree(
                bodyName: _resolvedBody!.Name,
                modeText: "No material assigned — pick one in the Material row above.",
                errorText: null);
            return;
        }

        TraceAllCurves(curves);
        UpdateModeAndSpecPickers();
    }

    private void ResetToError(string? message)
    {
        _resolvedBody = null;
        _profile = null;
        _perCurveTraceback.Clear();
        _blocks.SetSelectionInfo(Array.Empty<string>());
        RenderSheetMetalTree(bodyName: _resolvedBody?.Name, modeText: "Selection error", errorText: message);
    }

    private void TraceAllCurves(IReadOnlyList<NXObject> curves)
    {
        _perCurveTraceback.Clear();
        _hasBlockingUnstampedBead = false;

        foreach (var curve in curves)
        {
            var traceback = _tracebackService.Trace(curve);
            _perCurveTraceback.Add((curve, traceback));
            if (traceback.Result.HasUnstampedFeature)
                _hasBlockingUnstampedBead = true;
        }
    }

    private void UpdateModeAndSpecPickers()
    {
        UpdateSelectionInfoList();

        if (_hasBlockingUnstampedBead)
        {
            RenderSheetMetalTree(
                bodyName: _resolvedBody!.Name,
                modeText: "Blocked",
                errorText: "One or more selected curves already belong to a Bead feature not created by " +
                           "this tool (no SPEC recorded) — deselect it, or delete and recreate the bead.");
            return;
        }

        var stampedResults = _perCurveTraceback
            .Where(v => v.Traceback.Result.Found)
            .Select(v => v.Traceback.Result)
            .ToList();
        var distinctSpecs = stampedResults.Select(r => (r.StandardId, r.SpecId)).Distinct().ToList();

        var updateCount = stampedResults.Count;
        var createCount = _perCurveTraceback.Count - updateCount;

        // Pre-fill the pickers only when every stamped curve agrees on one SPEC — otherwise leave the
        // user's current choice alone, since there's no single "the" existing SPEC to default to.
        if (distinctSpecs.Count == 1 && distinctSpecs[0].StandardId is not null && distinctSpecs[0].SpecId is not null)
        {
            var tracedSpecId = distinctSpecs[0].SpecId!;
            _blocks.SelectStandard(distinctSpecs[0].StandardId!);
            OnStandardChanged(); // repopulates specs (valid-only) for that standard
            EnsureSpecInPicker(tracedSpecId); // a traced SPEC that no longer validates must still be selectable, so its failure is visible
            _blocks.SelectSpec(tracedSpecId);
        }

        // Re-validate the (possibly just pre-filled) SPEC against the current profile — a traced-back SPEC
        // can fail here if the sheet metal changed since it was created, and that must surface the same
        // "failed rule + alternatives" error OnSpecChanged shows for a user-driven pick.
        var (modeText, errorText) = CurrentModeAndErrorText();
        RenderSheetMetalTree(bodyName: _resolvedBody!.Name, modeText: modeText, errorText: errorText);
    }

    private void UpdateSelectionInfoList()
    {
        var lines = new List<string>();
        for (var i = 0; i < _perCurveTraceback.Count; i++)
        {
            var (curve, traceback) = _perCurveTraceback[i];
            var label = string.IsNullOrEmpty(curve.Name) ? $"Curve{i + 1}" : curve.Name;

            var status = traceback.Result switch
            {
                { HasUnstampedFeature: true } => "BLOCKED: no SPEC recorded",
                { Found: true, IsPatternInstance: true } => $"Editing SPEC {traceback.Result.SpecId} (via pattern)",
                { Found: true } => $"Editing SPEC {traceback.Result.SpecId}",
                _ => "New bead",
            };

            lines.Add($"{label} — {status}");
        }

        _blocks.SetSelectionInfo(lines);
    }

    /// <summary>Renders the ShtMetal tree from current presenter state plus the given mode/error override —
    /// centralizes what would otherwise be a dozen near-duplicate <c>PopulateSheetMetalTree</c> call sites.</summary>
    private void RenderSheetMetalTree(string? bodyName, string modeText, string? errorText)
    {
        var specPreview = CurrentSpecPreview();

        _blocks.PopulateSheetMetalTree(
            bodyName,
            _profile?.Thickness,
            _materialDisplayName,
            _materialMissing,
            _materialMissing ? _materialPicker.ListAssignableMaterials().Select(m => m.Name).ToList() : Array.Empty<string>(),
            modeText,
            specPreview?.Thickness,
            specPreview?.AllowedMaterialsSummary,
            errorText,
            OnMaterialPicked);

        _blocks.SetSpecPreview(specPreview?.Radius, specPreview?.Width, specPreview?.Height, specPreview?.DieRadius);
    }

    private void OnMaterialPicked(string materialName)
    {
        if (_resolvedBody is null)
            return;

        var result = _materialAssigner.Assign(_resolvedBody, materialName);
        if (!result.Ok)
        {
            _blocks.ShowError(result.Message ?? "Material assignment failed.");
            return;
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
            return;
        }

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

    private void RepopulateSpecPicker()
    {
        var specIds = _profile is not null
            ? _specFinder.FindValid(_profile, _currentStandardSpecs).Select(s => s.SpecId).ToList()
            : _currentStandardSpecs.Select(s => s.SpecId).ToList();

        _blocks.PopulateSpecs(specIds);
    }

    /// <summary>Adds <paramref name="specId"/> to the picker if it's a real row in the current Standard but
    /// didn't make the valid-only list <see cref="RepopulateSpecPicker"/> builds — otherwise a traced-back
    /// SPEC that's stopped validating (sheet metal changed since it was created) would have nothing to
    /// select, and its failure would have no way to become visible in the dialog.</summary>
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
        var updateCount = _perCurveTraceback.Count(v => v.Traceback.Result.Found);
        var createCount = _perCurveTraceback.Count - updateCount;
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
        if (_hasBlockingUnstampedBead)
        {
            _blocks.ShowError("Selection includes a bead not created by this tool — cannot apply.");
            return 1;
        }

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

        foreach (var (curve, traceback) in _perCurveTraceback)
        {
            var isUpdate = traceback.Result.Found;
            var result = _featureService.CreateOrUpdate(curve, spec, traceback.ExistingFeature);
            if (!result.Ok)
            {
                anyFailed = true;
                _context.Log.Error($"Bead {(isUpdate ? "update" : "create")} failed for curve '{curve.Name}': {result.Message}");
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
