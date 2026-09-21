using NXOpen;
using NXOpen.BlockStyler;
using NXOpen.UF;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.SheetMetal.Beads;
using BANxOpen.SheetMetal.Materials;

// NXOpen ships its own SelectObject (a selection API type) which collides with the BlockStyler UI block of
// the same name — aliased rather than fully qualified at each use, same as the NXOPEN Projects template.
using SelectObject = NXOpen.BlockStyler.SelectObject;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>All <c>TopBlock.FindBlock("stringId")</c> lookups and typed block reads/writes live here, per
/// Skills/with-block-ui.md §3 — when the Styler regenerates and renames/reorders blocks, only this file
/// changes. Block IDs below match the real <c>BLOCKUI_BEAD.dlx</c> exactly; see BEAD_DIALOG_BLOCKS.md.
///
/// Reads/writes go through the direct typed properties confirmed by reflecting NXOpenUI.dll —
/// <c>UIBlock.Show</c>/<c>.Label</c>, <c>Enumeration.ValueAsString</c>/<c>SetEnumMembers</c>/<c>GetEnumMembers</c>,
/// <c>DoubleBlock.Value</c>/<c>.ReadOnlyValue</c>, <c>ListBox.SetListItems</c>/<c>.GetSelectedItems</c>,
/// <c>ReverseDirection.Origin</c>/<c>.Direction</c>/<c>.Flip</c>, <c>Toggle.Value</c>, and the <c>Tree</c>
/// node/column/state API — rather than the generic <c>PropertyList</c> string-keyed API, except for
/// <c>SelectObject</c>'s selected-objects, which is its own typed <c>GetSelectedObjects()</c>.
///
/// Value-changing blocks (the enums, the selection, the button, the direction, the toggle) report through the
/// generated <c>BLOCKUI_BEAD.update_cb(UIBlock)</c>, dispatched by block-reference equality in that file. The two
/// trees and the selection list's delete button do NOT: NX calls handlers registered on those blocks themselves,
/// so those are registered here in <see cref="Initialize"/> and translated into <see cref="IBeadTreeSink"/> calls.</summary>
public sealed class BlockAccessor
{
    // ---- Block IDs — must match BLOCKUI_BEAD.dlx exactly. See BEAD_DIALOG_BLOCKS.md for the full table. ----
    internal const string CurvesId = "super_section0";
    internal const string BeadFeaturesId = "selection0";
    internal const string ClearAllButtonId = "btn_ClearAll";
    internal const string SelectionInfoListId = "list_SelectedObjects";
    internal const string PreferenceLabelId = "label_currentPref";
    internal const string MaterialTreeId = "ShtMetal";
    internal const string StandardEnumId = "enum_SmStd";
    internal const string MaterialFilterEnumId = "enum_SmMaterial";
    internal const string BeadSpecEnumId = "enum_BABead";
    internal const string BeadOptionsTreeId = "BeadOptions";
    internal const string RadiusDoubleId = "double_R";
    internal const string WidthDoubleId = "double_W";
    internal const string HeightDoubleId = "double_H";
    internal const string DieRadiusDoubleId = "double_PRAD";
    internal const string DirectionId = "direction0";
    internal const string PreviewToggleId = "togglePreview";

    // ---- Tree layouts ----
    // Column 0 carries the state icon AND the node's own text: NX draws a node's state icon at its label, not
    // at an arbitrary column, so the checkbox and the row's name necessarily share column 0.
    private static readonly (string Title, int Width)[] MaterialColumns =
        { ("Material", 220), ("Thickness", 80), ("Bend Radius", 100) };

    private static readonly (string Title, int Width)[] SpecColumns =
        { ("SPEC", 140), ("R", 60), ("W", 60), ("H", 60), ("P RAD", 60), ("t", 60) };

    // Node state values. 1 and 2 are NX's built-in unchecked/checked icons, so no StateIconName handler or
    // bitmap is needed. 0 is "no state icon at all" — a row left at 0 would look like it cannot be picked.
    private const int UncheckedState = 1;
    private const int CheckedState = 2;

    /// <summary>Foreground colours from NX's own palette, as BANxOpen.Ui.MaterialAssignment uses them.</summary>
    private const int WarningForegroundColor = 36;

    private readonly BlockDialog _dialog;
    private readonly Action<string>? _logWarning;

    private SuperSection? _curves;
    private SelectObject? _beadFeatures;
    private Button? _clearAllButton;
    private ListBox? _selectionInfoList;
    private NXOpen.BlockStyler.Label? _preferenceLabel;
    private Tree? _materialTree;
    private Enumeration? _standardEnum;
    private Enumeration? _materialFilterEnum;
    private Enumeration? _beadSpecEnum;
    private Tree? _beadOptionsTree;
    private DoubleBlock? _radiusDouble;
    private DoubleBlock? _widthDouble;
    private DoubleBlock? _heightDouble;
    private DoubleBlock? _dieRadiusDouble;
    private ReverseDirection? _direction;
    private Toggle? _previewToggle;

    private TreeBinding<SheetMetalMaterialRow>? _materials;
    private TreeBinding<BeadSpecRow>? _specs;
    private readonly HashSet<Tree> _treesWithColumns = new();

    // NX refuses Tree.InsertColumn/InsertNode until the dialog is shown, so tree writes before then are
    // skipped; OnDialogShown repopulates both trees once this is set.
    private bool _shown;

    // What each tree was last populated with, so a stale tree (see TreeBinding.IsStale) can be rebuilt.
    private Action? _repopulateMaterials;
    private Action? _repopulateSpecs;

    /// <summary>Standard display-name -> id, since the Standard enum shows DisplayName but callers need Id.</summary>
    private IReadOnlyDictionary<string, string> _standardIdsByDisplayName = new Dictionary<string, string>();

    public BlockAccessor(BlockDialog dialog, Action<string>? logWarning = null)
    {
        _dialog = dialog;
        _logWarning = logWarning;
    }

    /// <summary>Resolves every block and registers the tree and list callbacks. Called from the presenter's own
    /// Initialize, which the generated <c>initialize_cb</c> calls.</summary>
    public void Initialize(IBeadTreeSink sink)
    {
        _curves = TryFindBlock<SuperSection>(CurvesId);
        _beadFeatures = TryFindBlock<SelectObject>(BeadFeaturesId);
        _clearAllButton = TryFindBlock<Button>(ClearAllButtonId);
        _selectionInfoList = TryFindBlock<ListBox>(SelectionInfoListId);
        _preferenceLabel = TryFindBlock<NXOpen.BlockStyler.Label>(PreferenceLabelId);
        _materialTree = TryFindBlock<Tree>(MaterialTreeId);
        _standardEnum = TryFindBlock<Enumeration>(StandardEnumId);
        _materialFilterEnum = TryFindBlock<Enumeration>(MaterialFilterEnumId);
        _beadSpecEnum = TryFindBlock<Enumeration>(BeadSpecEnumId);
        _beadOptionsTree = TryFindBlock<Tree>(BeadOptionsTreeId);
        _radiusDouble = TryFindBlock<DoubleBlock>(RadiusDoubleId);
        _widthDouble = TryFindBlock<DoubleBlock>(WidthDoubleId);
        _heightDouble = TryFindBlock<DoubleBlock>(HeightDoubleId);
        _dieRadiusDouble = TryFindBlock<DoubleBlock>(DieRadiusDoubleId);
        _direction = TryFindBlock<ReverseDirection>(DirectionId);
        _previewToggle = TryFindBlock<Toggle>(PreviewToggleId);

        ConfigureSelection(sink);

        if (_materialTree is { } materialTree)
        {
            _materials = new TreeBinding<SheetMetalMaterialRow>(materialTree);
            materialTree.SetOnStateChangeHandler((_, node, _) =>
                Safe("ShtMetal.OnStateChange", () => sink.OnMaterialRowChecked(_materials!.Resolve(node))));
        }

        if (_beadOptionsTree is { } beadOptionsTree)
        {
            _specs = new TreeBinding<BeadSpecRow>(beadOptionsTree);
            beadOptionsTree.SetOnStateChangeHandler((_, node, _) =>
                Safe("BeadOptions.OnStateChange", () => sink.OnBeadSpecRowChecked(_specs!.Resolve(node))));
        }
    }

    /// <summary>Called once the dialog is shown: from here on the trees accept columns and rows.</summary>
    public void MarkShown() => _shown = true;

    /// <summary>Runs a block callback with its exceptions logged and shown rather than thrown. An exception
    /// escaping an NX callback is swallowed or fatal depending on the call path, and either way the user is
    /// left with a dialog that quietly stopped responding to clicks.</summary>
    private void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logWarning?.Invoke($"{what} failed: {ex}");
            NxMessageBoxHelper.ShowError($"The dialog hit an unexpected error handling {what}: {ex.Message}");
        }
    }

    // ---- Selection: curves (super_section0), Bead features (selection0), and the per-bead list ----

    /// <summary>Set here rather than in the Styler, so a regeneration cannot quietly bring body selection back.
    /// Each setting is applied on its own: one NX refuses must not skip the rest — above all the filter.
    ///
    /// selection0 ships single-select with no filter (the .dlx cannot carry one); it becomes many-select, solid
    /// features only. No filter narrows a feature to Beads, so a non-Bead feature is dropped by
    /// <c>BeadSelectionExpander</c>. The label goes through
    /// <c>LabelString</c>: this block has no property behind <c>UIBlock.Label</c>.
    ///
    /// super_section0 keeps its curve rules and sketch-on-the-fly from the .dlx; only its label and tooltip are
    /// set.
    ///
    /// list_SelectedObjects gets its delete button here too, so a regeneration cannot lose it.</summary>
    private void ConfigureSelection(IBeadTreeSink sink)
    {
        if (_beadFeatures is { } features)
        {
            TrySetup("selection0 select mode", () => features.SelectModeAsString = "Multiple");
            TrySetup("selection0 label", () => features.LabelString = "Select Bead Feature");
            TrySetup("selection0 tooltip", () => features.ToolTip = "Select existing Bead features to update to the chosen SPEC");
            // Solid features only: a bead is one, and this keeps sketch, curve and datum features out before the
            // pick. A plain feature mask (UF_feature_type) let a click on a sketch line select its sketch feature.
            // NX has no Bead-only filter member, so a non-Bead solid feature (a flange, say) can still be picked
            // and is then left out by BeadSelectionExpander.
            TrySetup("selection0 filter", () =>
            {
                features.ClearFilter();
                features.AddFilterMember(NXOpen.Select.FilterMember.SolidFeature);
            });
        }

        if (_curves is { } curves)
        {
            TrySetup("super_section0 label", () => curves.LabelString = "Select Curves or Sketch");
            TrySetup("super_section0 tooltip", () => curves.ToolTip = "Each connected chain of curves becomes one bead; draw a sketch on the fly if needed");
        }

        if (_selectionInfoList is { } list)
        {
            TrySetup("list_SelectedObjects delete button", () => list.ShowDeleteButton = true);
            TrySetup("list_SelectedObjects delete handler", () => list.SetDeleteHandler(listBox =>
            {
                Safe("list_SelectedObjects.Delete", () => sink.OnSelectionRowsDeleted(listBox.GetSelectedItems()));
                return 0;
            }));
        }
    }

    /// <summary>Applies one piece of block setup, logging rather than showing a failure: it only leaves a block
    /// looking or filtering differently, which is no reason to interrupt the user with a message box.</summary>
    private void TrySetup(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logWarning?.Invoke($"{what} not applied: {ex.Message}");
        }
    }

    /// <summary>What the curve block collected — sections of curves, as NX returns them — less anything deleted.
    /// Cancelling a sketch drawn on the fly rolls its curves back, and the block can still hand them out;
    /// touching one then throws.</summary>
    public IReadOnlyList<NXObject> GetCurveBlockObjects() => NotDeleted(_curves?.GetSelectedObjects(), CurvesId);

    public IReadOnlyList<NXObject> GetBeadFeatureBlockObjects() => NotDeleted(_beadFeatures?.GetSelectedObjects(), BeadFeaturesId);

    public void SetCurveBlockObjects(IReadOnlyList<TaggedObject> objects) =>
        _curves?.SetSelectedObjects(objects.ToArray());

    public void SetBeadFeatureBlockObjects(IReadOnlyList<TaggedObject> objects) =>
        _beadFeatures?.SetSelectedObjects(objects.ToArray());

    public void ClearSelection()
    {
        _curves?.SetSelectedObjects(Array.Empty<TaggedObject>());
        _beadFeatures?.SetSelectedObjects(Array.Empty<TaggedObject>());
    }

    /// <summary>Drops only what NX reports as deleted. Not "keep only UF_OBJ_ALIVE": the Section the curve block
    /// builds while the dialog is open is a temporary object, not an alive one, and is exactly what must be kept.</summary>
    private List<NXObject> NotDeleted(TaggedObject[]? objects, string blockId)
    {
        var result = new List<NXObject>();
        if (objects is null)
            return result;

        var ufSession = UFSession.GetUFSession();
        var dropped = new List<string>();
        foreach (var obj in objects.OfType<NXObject>())
        {
            int status;
            try
            {
                status = ufSession.Obj.AskStatus(obj.Tag);
            }
            catch (NXException)
            {
                status = UFConstants.UF_OBJ_DELETED;
            }

            if (status == UFConstants.UF_OBJ_DELETED)
                dropped.Add($"{obj.GetType().Name} tag {obj.Tag}");
            else
                result.Add(obj);
        }

        if (dropped.Count > 0)
            _logWarning?.Invoke($"{blockId}: {dropped.Count} selected object(s) no longer exist (e.g. a cancelled sketch) and were left out: {string.Join(", ", dropped)}.");

        return result;
    }

    public void SetSelectionInfo(IReadOnlyList<string> statusLines) =>
        _selectionInfoList?.SetListItems(statusLines.ToArray());

    // ---- Preference summary label ----

    public void SetPreferenceSummary(string text)
    {
        if (_preferenceLabel is not null)
            _preferenceLabel.Label = text;
    }

    // ---- Direction and preview ----

    /// <summary>False when the block is missing: the bead is then built to the section's normal side.</summary>
    public bool IsDirectionFlipped => _direction?.Flip ?? false;

    /// <summary>Places the direction arrow, or hides the block when there is nothing to point at.</summary>
    public void SetDirection(Point3d? origin, Vector3d? direction)
    {
        if (_direction is null)
            return;

        if (origin is { } o && direction is { } d)
        {
            _direction.Origin = o;
            _direction.Direction = d;
            _direction.Show = true;
        }
        else
        {
            _direction.Show = false;
        }
    }

    public bool IsPreviewOn => _previewToggle?.Value ?? false;

    // ---- ShtMetal tree: the sheet metal material picker ----

    /// <summary>Rebuilds the tree from <paramref name="rows"/>, with <paramref name="checkedRow"/> checked and
    /// every other row unchecked.</summary>
    /// <param name="thicknessDiffers">Rows this returns true for are coloured as a warning — their thickness is
    /// not the body's. They stay selectable: picking one is reported, not prevented.</param>
    public void PopulateMaterialTree(
        IReadOnlyList<SheetMetalMaterialRow> rows,
        SheetMetalMaterialRow? checkedRow,
        Func<SheetMetalMaterialRow, bool> thicknessDiffers)
    {
        _repopulateMaterials = () => PopulateMaterialTree(rows, checkedRow, thicknessDiffers);
        if (!_shown || _materialTree is null || _materials is null)
            return;

        EnsureTreeColumns(_materialTree, MaterialColumns);

        _materials.Rebuild(() =>
        {
            foreach (var row in rows)
            {
                var node = _materials.Add(row.Name, row);
                node.SetColumnDisplayText(1, $"{row.Thickness:0.####}");
                node.SetColumnDisplayText(2, row.BendRadius);
                node.SetState(Matches(row, checkedRow) ? CheckedState : UncheckedState);

                if (thicknessDiffers(row))
                    node.ForegroundColor = WarningForegroundColor;
            }
        });
    }

    /// <summary>Re-asserts the check across every row: exactly one checked, the rest not. Used both to apply
    /// the preferences' preselection and to enforce the one-and-only rule after a click.</summary>
    public void SetCheckedMaterialRow(SheetMetalMaterialRow? row)
    {
        if (_materials is null)
            return;

        if (_materials.IsStale())
        {
            RebuildStaleTree("ShtMetal", _repopulateMaterials);
            return;
        }

        foreach (var (node, value) in _materials.Rows)
            node.SetState(Matches(value, row) ? CheckedState : UncheckedState);
    }

    /// <summary>Rows are compared by Name, the standards file's own unique column — not by reference, because
    /// <c>SheetMetalMaterialTable.RowsFor</c> builds a fresh list on every call, so the row the presenter is
    /// holding is rarely the same instance as the one now in the tree.</summary>
    private static bool Matches(SheetMetalMaterialRow row, SheetMetalMaterialRow? other) =>
        other is not null && string.Equals(row.Name, other.Name, StringComparison.OrdinalIgnoreCase);

    // ---- BeadOptions tree: the SPEC picker ----

    /// <summary>Rebuilds the tree from <paramref name="specs"/>, with <paramref name="checkedSpec"/> checked and
    /// every other row unchecked.</summary>
    /// <param name="isWarning">Rows this returns true for are coloured as a warning — kept on the list for a
    /// selected bead, but they do not validate against this sheet metal.</param>
    public void PopulateSpecTree(IReadOnlyList<BeadSpecRow> specs, BeadSpecRow? checkedSpec, Func<BeadSpecRow, bool> isWarning)
    {
        _repopulateSpecs = () => PopulateSpecTree(specs, checkedSpec, isWarning);
        if (!_shown || _beadOptionsTree is null || _specs is null)
            return;

        EnsureTreeColumns(_beadOptionsTree, SpecColumns);

        _specs.Rebuild(() =>
        {
            foreach (var spec in specs)
            {
                var node = _specs.Add(spec.SpecId, spec);
                node.SetColumnDisplayText(1, $"{spec.RadiusAndRadS:0.####}");
                node.SetColumnDisplayText(2, $"{spec.Width:0.####}");
                node.SetColumnDisplayText(3, $"{spec.Height:0.####}");
                node.SetColumnDisplayText(4, $"{spec.DieRadiusP:0.####}");
                node.SetColumnDisplayText(5, $"{spec.Thickness:0.####}");
                node.SetState(Matches(spec, checkedSpec) ? CheckedState : UncheckedState);

                if (isWarning(spec))
                    node.ForegroundColor = WarningForegroundColor;
            }
        });
    }

    /// <summary>As <see cref="SetCheckedMaterialRow"/>, for the SPEC tree.</summary>
    public void SetCheckedSpecRow(BeadSpecRow? spec)
    {
        if (_specs is null)
            return;

        if (_specs.IsStale())
        {
            RebuildStaleTree("BeadOptions", _repopulateSpecs);
            return;
        }

        foreach (var (node, value) in _specs.Rows)
            node.SetState(Matches(value, spec) ? CheckedState : UncheckedState);
    }

    /// <summary>By workbook and SPEC id: two of a Standard's bead SPEC workbooks could carry the same SpecId.</summary>
    internal static bool Matches(BeadSpecRow row, BeadSpecRow? other) =>
        other is not null
        && string.Equals(row.SpecId, other.SpecId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(row.WorkbookName, other.WorkbookName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rebuilds both trees from what they were last populated with, when either no longer holds its own
    /// nodes. Part of recovering the dialog after an exception in a callback.</summary>
    public void RebuildStaleTrees()
    {
        if (_materials?.IsStale() == true)
            RebuildStaleTree("ShtMetal", _repopulateMaterials);

        if (_specs?.IsStale() == true)
            RebuildStaleTree("BeadOptions", _repopulateSpecs);
    }

    private void RebuildStaleTree(string treeId, Action? repopulate)
    {
        _logWarning?.Invoke($"{treeId}: the tree no longer held its rows (e.g. after a cancelled sketch); rebuilding it.");
        repopulate?.Invoke();
    }

    private void EnsureTreeColumns(Tree tree, IReadOnlyList<(string Title, int Width)> columns)
    {
        if (!_treesWithColumns.Add(tree))
            return;

        for (var i = 0; i < columns.Count; i++)
        {
            tree.InsertColumn(i, columns[i].Title, columns[i].Width);
            tree.SetColumnResizePolicy(i, Tree.ColumnResizePolicy.ConstantWidth);
        }
    }

    // ---- Sheet metal material filter ----

    /// <summary>Replaces the filter's members with <paramref name="options"/>. The first should be a "choose"
    /// prompt: an Enumeration always has a value, and a material must never be chosen for the user by
    /// default.</summary>
    public void PopulateMaterialFilter(IReadOnlyList<string> options) =>
        _materialFilterEnum?.SetEnumMembers(options.ToArray());

    public string? GetSelectedMaterialFilter() => _materialFilterEnum?.ValueAsString;

    public void SelectMaterialFilter(string option)
    {
        if (_materialFilterEnum is not null)
            _materialFilterEnum.ValueAsString = option;
    }

    // ---- Standard ----

    public void PopulateStandards(IReadOnlyList<(string Id, string DisplayName)> standards)
    {
        _standardIdsByDisplayName = standards.ToDictionary(s => s.DisplayName, s => s.Id);
        _standardEnum?.SetEnumMembers(standards.Select(s => s.DisplayName).ToArray());
    }

    public string? GetSelectedStandardId() =>
        _standardEnum?.ValueAsString is { } displayName && _standardIdsByDisplayName.TryGetValue(displayName, out var id)
            ? id
            : null;

    public void SelectStandard(string standardId)
    {
        var displayName = _standardIdsByDisplayName.FirstOrDefault(kv => kv.Value == standardId).Key;
        if (_standardEnum is not null && displayName is not null)
            _standardEnum.ValueAsString = displayName;
    }

    // ---- Bead SPEC (the radio box) ----

    /// <summary>The bead SPECs the dialog offers, read from the block rather than restated in code: the .dlx is
    /// the source of truth for them, so adding a third SPEC is a Styler edit plus a matching workbook, with no
    /// code change.</summary>
    public IReadOnlyList<string> GetBeadSpecNames() =>
        _beadSpecEnum?.GetEnumMembers() ?? Array.Empty<string>();

    public string? GetSelectedBeadSpec() => _beadSpecEnum?.ValueAsString;

    public void SelectBeadSpec(string beadSpec)
    {
        if (_beadSpecEnum is not null)
            _beadSpecEnum.ValueAsString = beadSpec;
    }

    // ---- SPEC preview (R/W/H/Die Radius) — locked read-only ----

    public void SetSpecPreview(double? radius, double? width, double? height, double? dieRadius)
    {
        SetLockedDouble(_radiusDouble, radius);
        SetLockedDouble(_widthDouble, width);
        SetLockedDouble(_heightDouble, height);
        SetLockedDouble(_dieRadiusDouble, dieRadius);
    }

    /// <summary>The .dlx already ships these blocks insensitive, so ReadOnlyValue is belt-and-braces — kept so
    /// the read-only behaviour does not silently depend on a Styler property surviving a regeneration.</summary>
    private static void SetLockedDouble(DoubleBlock? block, double? value)
    {
        if (block is null)
            return;

        block.Value = value ?? 0.0;
        block.ReadOnlyValue = true;
    }

    // ---- Generic dialogs — forwards to the shared foundation helper, per with-block-ui.md §3. ----

    public bool Confirm(string message) => NxMessageBoxHelper.Confirm(message);

    public void ShowResult(OperationResult result, string successMessage) => NxMessageBoxHelper.ShowResult(result, successMessage);

    public void ShowError(string message) => NxMessageBoxHelper.ShowError(message);

    // ---- Low-level helpers ----

    private T? TryFindBlock<T>(string blockId) where T : class
    {
        try
        {
            var block = _dialog.TopBlock.FindBlock(blockId) as T;
            if (block is null)
                _logWarning?.Invoke($"Dialog block '{blockId}' is missing or is not a {typeof(T).Name}; the features that use it are disabled.");

            return block;
        }
        catch (Exception ex)
        {
            _logWarning?.Invoke($"Dialog block '{blockId}' could not be resolved ({ex.Message}); the features that use it are disabled.");
            return null;
        }
    }
}
