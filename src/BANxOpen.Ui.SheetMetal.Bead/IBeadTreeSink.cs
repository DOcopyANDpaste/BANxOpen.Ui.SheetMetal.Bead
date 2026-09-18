using BANxOpen.SheetMetal.Materials;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>What <see cref="BlockAccessor"/> calls back into when the user interacts with the ShtMetal tree.
///
/// It exists so <see cref="BeadDialogPresenter"/> never sees an NXOpen <c>Node</c> or <c>Tree</c>: the accessor
/// resolves the clicked node to the material row it was rendered from and hands that over. Same seam as
/// <c>ITreeInteractionSink</c> in BANxOpen.Ui.MaterialAssignment, narrowed to this dialog's one interaction.
///
/// Tree events do NOT route through the generated <c>update_cb</c> — NX calls handlers registered directly on
/// the <c>Tree</c> block — which is why this seam exists here rather than as another branch in BLOCKUI_BEAD.cs.</summary>
public interface IBeadTreeSink
{
    /// <summary>The user clicked a row's checkbox. <paramref name="row"/> is null when the click could not be
    /// resolved to a row, which the presenter treats as "leave the current pick alone" rather than as a
    /// deselection.</summary>
    void OnMaterialRowChecked(SheetMetalMaterialRow? row);
}
