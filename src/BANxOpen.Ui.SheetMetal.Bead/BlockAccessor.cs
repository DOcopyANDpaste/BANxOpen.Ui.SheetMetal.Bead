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
/// <c>DoubleBlock.Value</c>/<c>.ReadOnlyValue</c>, <c>ListBox.SetListItems</c>/<c>.SelectedItemString</c>, and
/// the <c>Tree</c> node/column/state API — rather than the generic <c>PropertyList</c> string-keyed API,
/// except for <c>SelectObject</c>'s selected-objects, which is its own typed <c>GetSelectedObjects()</c>.
///
/// Value-changing blocks (the enums, the list, the selection, the button) report through the generated
/// <c>BLOCKUI_BEAD.update_cb(UIBlock)</c>, dispatched by block-reference equality in that file. The ShtMetal
/// tree does NOT: NX calls handlers registered on the Tree block itself, so those are registered here in
/// <see cref="Initialize"/> and translated into <see cref="IBeadTreeSink"/> calls.</summary>
public sealed class BlockAccessor
{
    // ---- Block IDs — must match BLOCKUI_BEAD.dlx exactly. See BEAD_DIALOG_BLOCKS.md for the full table. ----
    internal const string CurvesId = "super_section0";
    internal const string BeadFeaturesId = "selection0";
    internal const string ClearAllButtonId = "btn_ClearAll";
    internal const string SelectionInfoListId = "list_SelectedObjects";
    internal const string MaterialTreeId = "ShtMetal";
    internal const string StandardEnumId = "enum_SmStd";
    internal const string MaterialFilterEnumId = "enum_SmMaterial";
    internal const string BeadSpecEnumId = "enum_BABead";
    internal const string SpecVariantListId = "list_BeadSpecVariants";
    internal const string RadiusDoubleId = "double_R";
    internal const string WidthDoubleId = "double_W";
    internal const string HeightDoubleId = "double_H";
    internal const string DieRadiusDoubleId = "double_PRAD";

    // ---- ShtMetal tree layout ----
    // Column 0 carries the state icon AND the node's own text: NX draws a node's state icon at its label, not
    // at an arbitrary column, so the checkbox and the material name necessarily share column 0.
    private const int MaterialColumn = 0;
    private const int ThicknessColumn = 1;
    private const int BendRadiusColumn = 2;

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
    private Tree? _materialTree;
    private Enumeration? _standardEnum;
    private Enumeration? _materialFilterEnum;
    private Enumeration? _beadSpecEnum;
    private ListBox? _specVariantList;
    private DoubleBlock? _radiusDouble;
    private DoubleBlock? _widthDouble;
    private DoubleBlock? _heightDouble;
    private DoubleBlock? _dieRadiusDouble;

    private TreeBinding<SheetMetalMaterialRow>? _materials;
    private bool _treeColumnsReady;

    /// <summary>Standard display-name -> id, since the Standard enum shows DisplayName but callers need Id.</summary>
    private IReadOnlyDictionary<string, string> _standardIdsByDisplayName = new Dictionary<string, string>();

    /// <summary>Spec-variant display line -> the row it was rendered from, so a selection is never turned back
    /// into a domain value by parsing what the list shows.</summary>
    private IReadOnlyDictionary<string, BeadSpecRow> _specsByLine = new Dictionary<string, BeadSpecRow>();

    public BlockAccessor(BlockDialog dialog, Action<string>? logWarning = null)
    {
        _dialog = dialog;
        _logWarning = logWarning;
    }

    /// <summary>Resolves every block and registers the tree's callbacks. Called from the presenter's own
    /// Initialize, which the generated <c>initialize_cb</c> calls.</summary>
    public void Initialize(IBeadTreeSink sink)
    {
        _curves = TryFindBlock<SuperSection>(CurvesId);
        _beadFeatures = TryFindBlock<SelectObject>(BeadFeaturesId);
        _clearAllButton = TryFindBlock<Button>(ClearAllButtonId);
        _selectionInfoList = TryFindBlock<ListBox>(SelectionInfoListId);
        _materialTree = TryFindBlock<Tree>(MaterialTreeId);
        _standardEnum = TryFindBlock<Enumeration>(StandardEnumId);
        _materialFilterEnum = TryFindBlock<Enumeration>(MaterialFilterEnumId);
        _beadSpecEnum = TryFindBlock<Enumeration>(BeadSpecEnumId);
        _specVariantList = TryFindBlock<ListBox>(SpecVariantListId);
        _radiusDouble = TryFindBlock<DoubleBlock>(RadiusDoubleId);
        _widthDouble = TryFindBlock<DoubleBlock>(WidthDoubleId);
        _heightDouble = TryFindBlock<DoubleBlock>(HeightDoubleId);
        _dieRadiusDouble = TryFindBlock<DoubleBlock>(DieRadiusDoubleId);

        ConfigureSelection();

        if (_materialTree is null)
            return;

        _materials = new TreeBinding<SheetMetalMaterialRow>(_materialTree);

        _materialTree.SetOnStateChangeHandler((_, node, _) =>
            Safe("ShtMetal.OnStateChange", () => sink.OnMaterialRowChecked(_materials!.Resolve(node))));
    }

    /// <summary>Runs a tree callback with its exceptions logged and shown rather than thrown. An exception
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

    // ---- Selection: curves (super_section0) and Bead features (selection0) ----

    /// <summary>Set here rather than in the Styler, so a regeneration cannot quietly bring body selection back.
    /// Each setting is applied on its own: one NX refuses must not skip the rest — above all the filter.
    ///
    /// selection0 ships single-select with no filter; it becomes many-select, features only. No mask narrows a
    /// feature to Beads, so a non-Bead feature is dropped by <c>BeadSelectionExpander</c>. The label goes through
    /// <c>LabelString</c>: this block has no property behind <c>UIBlock.Label</c>.
    ///
    /// super_section0 keeps its curve rules and sketch-on-the-fly from the .dlx; only its label and tooltip are
    /// set.</summary>
    private void ConfigureSelection()
    {
        if (_beadFeatures is { } features)
        {
            TrySetup("selection0 select mode", () => features.SelectModeAsString = "Multiple");
            TrySetup("selection0 label", () => features.LabelString = "Select Bead Feature");
            TrySetup("selection0 tooltip", () => features.ToolTip = "Select existing Bead features to update to the chosen SPEC");
            TrySetup("selection0 filter", () => features.SetSelectionFilter(
                Selection.SelectionAction.ClearAndEnableSpecific,
                new[] { new Selection.MaskTriple(UFConstants.UF_feature_type, 0, 0) }));
        }

        if (_curves is { } curves)
        {
            TrySetup("super_section0 label", () => curves.LabelString = "Select Curves or Sketch");
            TrySetup("super_section0 tooltip", () => curves.ToolTip = "Each chain of curves becomes one bead; draw a sketch on the fly if needed");
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

    /// <summary>What the curve block collected — sections of curves, as NX returns them.</summary>
    public IReadOnlyList<NXObject> GetCurveBlockObjects() =>
        _curves?.GetSelectedObjects().OfType<NXObject>().ToList() ?? new List<NXObject>();

    public IReadOnlyList<NXObject> GetBeadFeatureBlockObjects() =>
        _beadFeatures?.GetSelectedObjects().OfType<NXObject>().ToList() ?? new List<NXObject>();

    public void ClearSelection()
    {
        _curves?.SetSelectedObjects(Array.Empty<TaggedObject>());
        _beadFeatures?.SetSelectedObjects(Array.Empty<TaggedObject>());
    }

    // ---- Per-item selection status list ----

    public void SetSelectionInfo(IReadOnlyList<string> statusLines) =>
        _selectionInfoList?.SetListItems(statusLines.ToArray());

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
        if (_materialTree is null || _materials is null)
            return;

        EnsureTreeColumns();

        _materials.Rebuild(() =>
        {
            foreach (var row in rows)
            {
                var node = _materials.Add(row.Name, row);
                node.SetColumnDisplayText(ThicknessColumn, $"{row.Thickness:0.####}");
                node.SetColumnDisplayText(BendRadiusColumn, row.BendRadius);
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

        foreach (var (node, value) in _materials.Rows)
            node.SetState(Matches(value, row) ? CheckedState : UncheckedState);
    }

    /// <summary>Rows are compared by Name, the standards file's own unique column — not by reference, because
    /// <c>SheetMetalMaterialTable.RowsFor</c> builds a fresh list on every call, so the row the presenter is
    /// holding is rarely the same instance as the one now in the tree.</summary>
    private static bool Matches(SheetMetalMaterialRow row, SheetMetalMaterialRow? other) =>
        other is not null && string.Equals(row.Name, other.Name, StringComparison.OrdinalIgnoreCase);

    private void EnsureTreeColumns()
    {
        if (_treeColumnsReady || _materialTree is null)
            return;

        InsertColumn(MaterialColumn, "Material", 220);
        InsertColumn(ThicknessColumn, "Thickness", 80);
        InsertColumn(BendRadiusColumn, "Bend Radius", 100);
        _treeColumnsReady = true;
    }

    private void InsertColumn(int columnId, string title, int width)
    {
        _materialTree!.InsertColumn(columnId, title, width);
        _materialTree.SetColumnResizePolicy(columnId, Tree.ColumnResizePolicy.ConstantWidth);
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

    // ---- SPEC variants (the list under the radio) ----

    /// <summary>Shows one line per SPEC row, keeping the line-to-row mapping so a selection resolves back
    /// without parsing the text.</summary>
    public void PopulateSpecVariants(IReadOnlyList<BeadSpecRow> specs)
    {
        var byLine = new Dictionary<string, BeadSpecRow>();
        foreach (var spec in specs)
            byLine[DescribeSpec(spec)] = spec;

        _specsByLine = byLine;
        _specVariantList?.SetListItems(byLine.Keys.ToArray());
    }

    private static string DescribeSpec(BeadSpecRow spec) =>
        $"{spec.SpecId}   R {spec.RadiusAndRadS:0.####}  W {spec.Width:0.####}  " +
        $"H {spec.Height:0.####}  P {spec.DieRadiusP:0.####}   (t {spec.Thickness:0.####})";

    public BeadSpecRow? GetSelectedSpecVariant() =>
        _specVariantList?.SelectedItemString is { } line && _specsByLine.TryGetValue(line, out var spec) ? spec : null;

    public void SelectSpecVariant(BeadSpecRow spec)
    {
        var line = _specsByLine.FirstOrDefault(kv => kv.Value.SpecId == spec.SpecId).Key;
        if (_specVariantList is not null && line is not null)
            _specVariantList.SetSelectedItemStrings(new[] { line });
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
