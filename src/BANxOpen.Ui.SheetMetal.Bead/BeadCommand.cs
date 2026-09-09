using BANxOpen.SheetMetal.Beads;
using BANxOpen.SheetMetal.Beads.Rules;
using BANxOpen.SheetMetal.Materials;
using BANxOpen.SheetMetal.SpecData;
using NXOpen;
using BANxOpen.SheetMetal.NxAdapters.Common;
using BANxOpen.SheetMetal.NxAdapters.Materials;
using BANxOpen.SheetMetal.NxAdapters.Beads;
using BANxOpen.Ui.SheetMetal.Bead;
using BANxOpen.Foundation.Core.RuleEngine;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.SheetMetal.Common;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>Entry point NX invokes from a MenuScript/ribbon action, per Skills/without-block-ui.md §1 and
/// matching NXOPEN Projects\NxAdapters\MaterialAssignmentCommand.cs. Composes the whole dependency graph
/// once per launch and shows the dialog. Keep this thin: wiring only, no business logic.
///
/// The dialog is the Styler-generated <c>BLOCKUI_BEAD</c> (root namespace, per its own generated file —
/// hand-edited to expose <c>TheDialog</c>/<c>Presenter</c>, see BLOCKUI_BEAD.cs's banner comments and
/// NxAdapters\Ui\BEAD_DIALOG_BLOCKS.md).</summary>
public static class BeadCommand
{
    // TODO: point at wherever these actually live for your deployment — a project-relative "config" folder
    // works for development; a shared network path is more realistic once this is rolled out to other users
    // (see the "Standard vs SPEC" and "material mapping" scope notes — both are meant to be user-editable
    // without a rebuild).
    private const string StandardsRegistryPath = @"config\standards.json";
    private const string MaterialGradeMapPath = @"config\material-grade-map.json";
    private const string SpecCacheDirectory = @"config\cache";

    public static void Main(string[] args)
    {
        if (!NxSessionContext.TryInitialize(out var context, out var failureReason))
        {
            UI.GetUI().NXMessageBox.Show("Bead", NXMessageBox.DialogType.Error, failureReason ?? "Could not start.");
            return;
        }

        // --- Core services (no NXOpen types) ---
        var validator = new BeadSpecValidator(new IGateRule<BeadValidationContext, RuleOutcome>[]
        {
            new ThicknessMatchRule(),
            new MaterialAllowedRule(),
        });
        var specFinder = new BeadSpecFinder(validator);

        var standardRegistry = new StandardRegistry(StandardsRegistryPath);
        var excelParser = new ExcelBeadSpecParser();
        var specSource = new FileSystemBeadSpecSource(standardRegistry, excelParser);
        var specCache = new BeadSpecCache(specSource, SpecCacheDirectory);

        MaterialGradeMap gradeMap;
        try
        {
            gradeMap = MaterialGradeMap.Load(MaterialGradeMapPath);
        }
        catch (Exception ex)
        {
            UI.GetUI().NXMessageBox.Show("Bead", NXMessageBox.DialogType.Error,
                $"Could not load the material grade map at '{MaterialGradeMapPath}': {ex.Message}");
            return;
        }

        // --- NxAdapters services (all NXOpen calls) ---
        var curveSetValidator = new SelectedCurveSetValidator(context);
        var profileReader = new SheetMetalProfileReader(context, gradeMap);
        var tracebackService = new BeadTracebackService(context);
        var expressionService = new ExpressionService(context);
        var featureService = new BeadFeatureService(context, expressionService);
        var materialPicker = new MaterialPickerStub();
        var materialAssigner = new MaterialAssigner(context);

        // --- Dialog ---
        var dialog = new BLOCKUI_BEAD();
        var blocks = new BlockAccessor(dialog.TheDialog, context.Log.Warn);
        var presenter = new BeadDialogPresenter(
            context, blocks, specCache, validator, specFinder, curveSetValidator, profileReader,
            tracebackService, featureService, materialPicker, materialAssigner);
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
