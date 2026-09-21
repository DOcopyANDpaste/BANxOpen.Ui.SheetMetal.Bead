using BANxOpen.SheetMetal.Beads;
using BANxOpen.SheetMetal.Materials;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>What <see cref="BlockAccessor"/> calls back into when the user interacts with a block whose events do
/// not route through the generated <c>update_cb</c>: the two trees, and the selection list's delete button.
///
/// It exists so <see cref="BeadDialogPresenter"/> never sees an NXOpen <c>Node</c> or <c>Tree</c>: the accessor
/// resolves the clicked node to the row it was rendered from and hands that over. Same seam as
/// <c>ITreeInteractionSink</c> in BANxOpen.Ui.MaterialAssignment.
///
/// NX calls these handlers registered directly on the block, which is why this seam exists here rather than as
/// more branches in BLOCKUI_BEAD.cs.</summary>
public interface IBeadTreeSink
{
    /// <summary>The user clicked a ShtMetal row's checkbox. <paramref name="row"/> is null when the click could not
    /// be resolved to a row, which the presenter treats as "leave the current pick alone" rather than as a
    /// deselection.</summary>
    void OnMaterialRowChecked(SheetMetalMaterialRow? row);

    /// <summary>The user clicked a BeadOptions row's checkbox. Null as for <see cref="OnMaterialRowChecked"/>.</summary>
    void OnBeadSpecRowChecked(BeadSpecRow? row);

    /// <summary>The user pressed the selection list's delete button. <paramref name="indices"/> are the list lines
    /// that were selected, in list order.</summary>
    void OnSelectionRowsDeleted(IReadOnlyList<int> indices);
}
