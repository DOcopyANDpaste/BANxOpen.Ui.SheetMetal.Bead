using BANxOpen.Foundation.Core.RuleEngine;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.SheetMetal.Beads;
using BANxOpen.SheetMetal.Beads.Rules;
using BANxOpen.SheetMetal.NxAdapters.Beads;
using BANxOpen.SheetMetal.NxAdapters.Common;
using NXOpen;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>Entry point NX invokes from a MenuScript/ribbon action, per Skills/without-block-ui.md §1.
/// Composes the whole dependency graph once per launch and shows the dialog. Keep this thin: wiring only,
/// no business logic.
///
/// The dialog is the Styler-generated <c>BLOCKUI_BEAD</c>, hand-edited to expose <c>TheDialog</c>/<c>Presenter</c>
/// — see BLOCKUI_BEAD.cs's banner comments and BEAD_DIALOG_BLOCKS.md.</summary>
public static class BeadCommand
{
    private const string Title = "Bead";

    public static void Main(string[] args)
    {
        if (!NxSessionContext.TryInitialize(out var context, out var failureReason))
        {
            UI.GetUI().NXMessageBox.Show(Title, NXMessageBox.DialogType.Error, failureReason ?? "Could not start.");
            return;
        }

        // --- Sheet metal config, bead SPECs and the Sheet Metal Preferences reader: the same objects the Material
        //     Assignment dialog builds, from the same config files. The Bead dialog only reads the preferences and
        //     never assigns materials, so no material engine is built here. ---
        var sheetMetal = SheetMetalServices.Create(context);
        if (!sheetMetal.Ok)
        {
            UI.GetUI().NXMessageBox.Show(Title, NXMessageBox.DialogType.Error, sheetMetal.Message ?? "Sheet metal configuration could not be loaded.");
            return;
        }

        var services = sheetMetal.Value!;

        // --- Bead SPEC validation ---
        var validator = new BeadSpecValidator(new IGateRule<BeadValidationContext, RuleOutcome>[]
        {
            new ThicknessMatchRule(),
            new MaterialAllowedRule(),
        });
        var specFinder = new BeadSpecFinder(validator);

        // --- Bead NX services ---
        // Bead-dialog-only, so built here rather than in the shared SheetMetalServices.
        var selectionExpander = new BeadSelectionExpander(context);
        var profileReader = new SheetMetalProfileReader(context, services.PreferenceService);
        var featureService = new BeadFeatureService(context, new ExpressionService(context), services.BeadSettings);

        // --- Dialog ---
        var dialog = new BLOCKUI_BEAD();
        var blocks = new BlockAccessor(dialog.TheDialog, context.Log.Warn);
        var presenter = new BeadDialogPresenter(
            context, blocks, services.SpecCache, validator, specFinder, selectionExpander,
            profileReader, services.TracebackService, featureService,
            services.SpecLookup, services.FeatureInventory, services.BeadSettings, services.PreferenceService,
            new BeadDirectionProbe(context), services.RequirementService);
        dialog.Presenter = presenter;

        try
        {
            dialog.Launch();
        }
        finally
        {
            // Removes a live bead preview however the dialog closed.
            presenter.Dispose();
            dialog.Dispose();
        }
    }

    // NX asks the assembly whether it can be unloaded — implemented so the DLL unloads predictably during development.
    public static int GetUnloadOption(string dummy) => (int)Session.LibraryUnloadOption.Immediately;
}
