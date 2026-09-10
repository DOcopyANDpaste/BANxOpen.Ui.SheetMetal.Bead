using NXOpen;
using NXOpen.BlockStyler;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Foundation.NxAdapters;

// NXOpen ships its own SelectObject (a selection API type) which collides with the BlockStyler UI block of
// the same name — aliased rather than fully qualified at each use, same as the NXOPEN Projects template.
using SelectObject = NXOpen.BlockStyler.SelectObject;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>All <c>TopBlock.FindBlock("stringId")</c> lookups and typed block reads/writes live here, per
/// Skills/with-block-ui.md §3 — when the Styler regenerates and renames/reorders blocks, only this file
/// changes. Block IDs below match the real <c>BLOCKUI_BEAD.dlx</c> exactly (reconciled against the
/// original placeholder layout — see the plan's "Dialog reconciled" section and BEAD_DIALOG_BLOCKS.md).
///
/// Reads/writes go through the direct typed properties confirmed by reflecting NXOpenUI.dll —
/// <c>UIBlock.Show</c>/<c>.Label</c>, <c>Enumeration.ValueAsString</c>/<c>SetEnumMembers</c>,
/// <c>DoubleBlock.Value</c>/<c>.ReadOnlyValue</c>, <c>ListBox.SetListItems</c>, and the <c>Tree</c>
/// node/column API (<c>InsertColumn</c>, <c>CreateNode</c>/<c>InsertNode</c>,
/// <c>Node.SetColumnDisplayText</c>) — rather than the generic <c>PropertyList</c> string-keyed API,
/// except for <c>SelectObject</c>'s selected-objects, which is its own typed <c>GetSelectedObjects()</c>.
///
/// No event/callback registration lives here on purpose: none of these block types expose a per-instance
/// "value changed"/"clicked" delegate. NX instead calls the generated <c>BLOCKUI_BEAD.update_cb(UIBlock)</c>
/// for any value-changing block (dispatched by block-reference equality in that file, hand-edited to
/// delegate straight into <see cref="BeadDialogPresenter"/> — see BLOCKUI_BEAD.cs). The one exception is
/// the <c>ShtMetal</c> tree's in-place Material editing, which genuinely does need callbacks registered on
/// the Tree instance itself (<c>SetAskEditControlHandler</c>/<c>SetOnEditOptionSelectedHandler</c>) — those
/// are wired here in <see cref="Initialize"/> since they're intrinsic to how the Tree block works, not
/// dialog-level plumbing.</summary>
public sealed class BlockAccessor
{
    // ---- Block IDs — must match BLOCKUI_BEAD.dlx exactly. See BEAD_DIALOG_BLOCKS.md for the full table. ----
    internal const string SelectedCurvesId = "selection0";
    internal const string ClearAllButtonId = "btn_ClearAll";
    internal const string SelectionInfoListId = "list_SelectionInfo";
    internal const string SheetMetalTreeId = "ShtMetal";
    internal const string StandardEnumId = "enum_BeadStd";
    internal const string SpecEnumId = "enum_BABead";
    internal const string RadiusDoubleId = "double_R";
    internal const string WidthDoubleId = "double_W";
    internal const string HeightDoubleId = "double_H";
    internal const string DieRadiusDoubleId = "double_PRAD";

    // ShtMetal tree layout: two columns, "Property" (0, the node's own label) and "Value" (1).
    private const int ValueColumn = 1;

    // Fixed row order/keys for the ShtMetal tree — rebuilt from scratch on every populate (cheap at 7 rows,
    // avoids stale-row bugs), so these are re-created each time rather than held as long-lived fields.
    private const string BodyRow = "Body";
    private const string ThicknessRow = "Thickness";
    private const string MaterialRow = "Material";
    private const string ModeRow = "Mode";
    private const string SpecThicknessRow = "SPEC Thickness";
    private const string AllowedMaterialsRow = "Allowed Materials";
    private const string ErrorRow = "Error";
    private const string WarningRow = "Warning";

    private readonly BlockDialog _dialog;
    private readonly Action<string>? _logWarning;

    private SelectObject? _selectedCurves;
    private Button? _clearAllButton;
    private ListBox? _selectionInfoList;
    private Tree? _sheetMetalTree;
    private Enumeration? _standardEnum;
    private Enumeration? _specEnum;
    private DoubleBlock? _radiusDouble;
    private DoubleBlock? _widthDouble;
    private DoubleBlock? _heightDouble;
    private DoubleBlock? _dieRadiusDouble;

    private bool _treeColumnsReady;
    private Node? _materialNode;
    private IReadOnlyList<string> _materialPickerOptions = Array.Empty<string>();
    private bool _materialRowIsEditable;

    /// <summary>Fired when the user picks a material from the in-tree combo — the presenter does the
    /// actual assignment and refresh; this class only reports what was picked.</summary>
    private Action<string>? _onMaterialPicked;

    /// <summary>Standard display-name -> id, since the Standard enum shows DisplayName but callers need Id.
    /// SPEC needs no such map — its display text already is the id.</summary>
    private IReadOnlyDictionary<string, string> _standardIdsByDisplayName = new Dictionary<string, string>();

    public BlockAccessor(BlockDialog dialog, Action<string>? logWarning = null)
    {
        _dialog = dialog;
        _logWarning = logWarning;
    }

    /// <summary>Resolves every block. Called from <c>initialize_cb</c>, per the NX samples.</summary>
    public void Initialize()
    {
        _selectedCurves = TryFindBlock<SelectObject>(SelectedCurvesId);
        _clearAllButton = TryFindBlock<Button>(ClearAllButtonId);
        _selectionInfoList = TryFindBlock<ListBox>(SelectionInfoListId);
        _sheetMetalTree = TryFindBlock<Tree>(SheetMetalTreeId);
        _standardEnum = TryFindBlock<Enumeration>(StandardEnumId);
        _specEnum = TryFindBlock<Enumeration>(SpecEnumId);
        _radiusDouble = TryFindBlock<DoubleBlock>(RadiusDoubleId);
        _widthDouble = TryFindBlock<DoubleBlock>(WidthDoubleId);
        _heightDouble = TryFindBlock<DoubleBlock>(HeightDoubleId);
        _dieRadiusDouble = TryFindBlock<DoubleBlock>(DieRadiusDoubleId);

        if (_sheetMetalTree is not null)
        {
            _sheetMetalTree.SetAskEditControlHandler(OnAskEditControl);
            _sheetMetalTree.SetOnEditOptionSelectedHandler(OnEditOptionSelected);
        }
    }

    // ---- Curve selection ----

    public IReadOnlyList<NXObject> GetSelectedCurves() =>
        _selectedCurves?.GetSelectedObjects().OfType<NXObject>().ToList() ?? new List<NXObject>();

    public void ClearSelection() => _selectedCurves?.SetSelectedObjects(Array.Empty<TaggedObject>());

    // ---- Per-item selection status list ----

    public void SetSelectionInfo(IReadOnlyList<string> statusLines) =>
        _selectionInfoList?.SetListItems(statusLines.ToArray());

    // ---- ShtMetal tree: body/thickness/material/mode/preview/error, all in one place ----

    /// <param name="onMaterialPicked">Called with the chosen material's name when the user picks one from
    /// the in-tree combo (only offered when <paramref name="materialMissing"/> is true and
    /// <paramref name="materialPickerOptions"/> is non-empty).</param>
    /// <param name="warningText">A non-blocking advisory, shown in its own row so it is not mistaken for an
    /// error that stops Apply.</param>
    public void PopulateSheetMetalTree(
        string? bodyName, double? thickness, string? materialDisplayName, bool materialMissing,
        IReadOnlyList<string> materialPickerOptions, string modeText,
        double? specThickness, string? allowedMaterialsSummary, string? errorText,
        Action<string> onMaterialPicked, string? warningText = null)
    {
        if (_sheetMetalTree is null)
            return;

        EnsureTreeColumns();
        _onMaterialPicked = onMaterialPicked;
        _materialPickerOptions = materialPickerOptions;
        _materialRowIsEditable = materialMissing && materialPickerOptions.Count > 0;

        // Frozen redraw + rebuild-from-scratch, per NXOPEN Projects' TreeBinding<T> — NX paints once
        // instead of once per node, and there's no Tree.Clear() so a full rebuild is the only reliable way
        // to avoid stale rows.
        _sheetMetalTree.Redraw(false);
        try
        {
            ClearTree();

            AddRow(BodyRow, bodyName ?? "(no selection)");
            AddRow(ThicknessRow, thickness.HasValue ? $"{thickness:0.###}" : "(unknown)");
            _materialNode = AddRow(MaterialRow, materialMissing ? "(none — click to assign)" : materialDisplayName ?? "");
            AddRow(ModeRow, modeText);
            AddRow(SpecThicknessRow, specThickness.HasValue ? $"{specThickness:0.###}" : "");
            AddRow(AllowedMaterialsRow, allowedMaterialsSummary ?? "");

            if (!string.IsNullOrEmpty(errorText))
                AddRow(ErrorRow, errorText!);

            if (!string.IsNullOrEmpty(warningText))
                AddRow(WarningRow, warningText!);
        }
        finally
        {
            _sheetMetalTree.Redraw(true);
        }
    }

    private void EnsureTreeColumns()
    {
        if (_treeColumnsReady || _sheetMetalTree is null)
            return;

        _sheetMetalTree.InsertColumn(0, "Property", 140);
        _sheetMetalTree.InsertColumn(ValueColumn, "Value", 220);
        _treeColumnsReady = true;
    }

    private void ClearTree()
    {
        if (_sheetMetalTree is null)
            return;

        // Collect roots before deleting: DeleteNode invalidates the node it removes, so walking the
        // sibling chain while deleting from it would step off a dead node (same reasoning as TreeBinding<T>
        // in NXOPEN Projects — there is no Tree.Clear()). Tree.RootNode is the first top-level node itself,
        // not an invisible container, so siblings are walked via NextSiblingNode.
        var roots = new List<Node>();
        for (var node = _sheetMetalTree.RootNode; node is not null; node = node.NextSiblingNode)
            roots.Add(node);

        foreach (var root in roots)
            _sheetMetalTree.DeleteNode(root);
    }

    private Node? AddRow(string propertyName, string value)
    {
        if (_sheetMetalTree is null)
            return null;

        var node = _sheetMetalTree.CreateNode(propertyName);
        _sheetMetalTree.InsertNode(node, null, null, Tree.NodeInsertOption.AlwaysLast);
        node.SetColumnDisplayText(ValueColumn, value);
        return node;
    }

    private Tree.ControlType OnAskEditControl(Tree tree, Node node, int columnId)
    {
        if (columnId != ValueColumn || !_materialRowIsEditable || _materialNode is null || !node.Tag.Equals(_materialNode.Tag))
            return Tree.ControlType.None;

        tree.SetEditOptions(_materialPickerOptions.ToArray(), columnId);
        return Tree.ControlType.ComboBox;
    }

    private Tree.EditControlOption OnEditOptionSelected(
        Tree tree, Node node, int columnId, int selectedOptionId, string selectedOptionText, Tree.ControlType type)
    {
        if (_materialNode is null || !node.Tag.Equals(_materialNode.Tag) || string.IsNullOrEmpty(selectedOptionText))
            return Tree.EditControlOption.Reject;

        _onMaterialPicked?.Invoke(selectedOptionText);
        return Tree.EditControlOption.Accept;
    }

    // ---- Standard / SPEC pickers ----

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

    public void PopulateSpecs(IReadOnlyList<string> specIds) => _specEnum?.SetEnumMembers(specIds.ToArray());

    public string? GetSelectedSpecId() => _specEnum?.ValueAsString;

    public void SelectSpec(string specId)
    {
        if (_specEnum is not null)
            _specEnum.ValueAsString = specId;
    }

    // ---- SPEC preview (R/W/H/Die Radius) — locked, per your "read-only preview" decision ----

    public void SetSpecPreview(double? radius, double? width, double? height, double? dieRadius)
    {
        SetLockedDouble(_radiusDouble, radius);
        SetLockedDouble(_widthDouble, width);
        SetLockedDouble(_heightDouble, height);
        SetLockedDouble(_dieRadiusDouble, dieRadius);
    }

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
