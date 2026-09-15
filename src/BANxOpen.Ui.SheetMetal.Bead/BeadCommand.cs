using BANxOpen.Foundation.Core.Materials.Library;
using BANxOpen.Foundation.Core.RuleEngine;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.Foundation.NxAdapters.Materials;
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

        // --- Sheet metal config, bead constraints and Sheet Metal Preferences sync: the same objects the Material
        //     Assignment dialog builds, from the same config files, so both dialogs judge a body by the same rules. ---
        var sheetMetal = SheetMetalServices.Create(context);
        if (!sheetMetal.Ok)
        {
            UI.GetUI().NXMessageBox.Show(Title, NXMessageBox.DialogType.Error, sheetMetal.Message ?? "Sheet metal configuration could not be loaded.");
            return;
        }

        var services = sheetMetal.Value!;

        // --- Shared material engine: the same rule modules the Material Assignment dialog registers ---
        var bodyResolver = new BodyResolver(context);
        var libraryRepository = new FileSystemMaterialLibraryRepository(onWarning: context.Log.Warn);
        var libraryLoader = new CachingMaterialLibraryLoader(libraryRepository, new MaterialLibraryParser());

        var engine = MaterialEngine.Create(context, bodyResolver, libraryRepository.RootDirectory, services.MaterialModules);
        if (!engine.Ok)
        {
            UI.GetUI().NXMessageBox.Show(Title, NXMessageBox.DialogType.Error, engine.Message ?? "Material rules could not be loaded.");
            return;
        }

        var materialAssignment = new SheetMetalMaterialAssignment(
            context,
            engine.Value!,
            libraryRepository,
            libraryLoader,
            services.GradeMap);

        // --- Bead SPEC validation ---
        var validator = new BeadSpecValidator(new IGateRule<BeadValidationContext, RuleOutcome>[]
        {
            new ThicknessMatchRule(),
            new MaterialAllowedRule(),
        });
        var specFinder = new BeadSpecFinder(validator);

        // --- Bead NX services ---
        var curveSetValidator = new SelectedCurveSetValidator(context);
        var profileReader = new SheetMetalProfileReader(context, services.GradeMap, services.PreferenceService);
        var featureService = new BeadFeatureService(context, new ExpressionService(context), services.BeadSettings);

        // --- Dialog ---
        var dialog = new BLOCKUI_BEAD();
        var blocks = new BlockAccessor(dialog.TheDialog, context.Log.Warn);
        var presenter = new BeadDialogPresenter(
            context, blocks, services.SpecCache, validator, specFinder, curveSetValidator, profileReader,
            services.TracebackService, featureService, materialAssignment,
            services.SpecLookup, services.GeometryReader, services.BeadSettings, services.PreferenceService);
        dialog.Presenter = presenter;

        try
        {
            dialog.Launch();
        }
        finally
        {
            dialog.Dispose();
        }
    }

    // NX asks the assembly whether it can be unloaded — implemented so the DLL unloads predictably during development.
    public static int GetUnloadOption(string dummy) => (int)Session.LibraryUnloadOption.Immediately;
}
