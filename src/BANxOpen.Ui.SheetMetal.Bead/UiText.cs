namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>The text that exists because of the Bead dialog's design: block labels, the SPEC tree's columns, the
/// selection list, the preferences label, the dialog's blocking errors and Apply messages. Change a wording here and
/// rebuild; keep the <c>{…}</c> placeholders, which are the values the text is filled with.
///
/// Not here, by design (see the nxopen-csharp-patterns skill, common.md §8):
/// <list type="bullet">
/// <item>What a rule, check or service says — bead SPEC validation, the state of the Sheet Metal Preferences, why a bead
/// is unidentified, requirement notes, configuration and SPEC-workbook errors — is worded by <c>SheetMetalText</c> (or
/// <c>FoundationText</c>) and shown as-is, so the Material Assignment dialog says it the same way.</item>
/// <item>Message box titles, "could not start", the unexpected-error message and NX error formatting are
/// <c>NxDialogText</c>'s, shared by every NX dialog.</item>
/// <item>Log lines — the status block, mode text and advisory warnings written to the Listing Window — are not user
/// text.</item>
/// </list></summary>
internal static class UiText
{
    private static readonly string Para = Environment.NewLine + Environment.NewLine;

    // ---- Message box title and block labels ----

    public static class Titles
    {
        public const string Dialog = "Bead";
    }

    public static class Blocks
    {
        public const string SelectBeadFeature = "Select Bead Feature";
        public const string SelectBeadFeatureTip = "Select existing Bead features to update to the chosen SPEC";
        public const string SelectCurves = "Select Curves or Sketch";
        public const string SelectCurvesTip = "Each connected chain of curves becomes one bead; draw a sketch on the fly if needed";
    }

    /// <summary>The SPEC tree's columns, with their widths.</summary>
    public static class Columns
    {
        public const string Spec = "SPEC";
        public const string Radius = "R";
        public const string Width = "W";
        public const string Height = "H";
        public const string DieRadius = "P RAD";
        public const string Thickness = "t";
    }

    // ---- The selection list (list_SelectedObjects) ----

    public static class Selection
    {
        public static string Line(string label, string status) => $"{label} — {status}";

        public static string Error(string error) => $"ERROR: {error}";
        public static string UnstampedMatched(string spec) => $"Not created by this tool; geometry matches SPEC {spec} — Apply records it";
        public static string UnstampedUnmatched(string? reason) => $"WARNING: not created by this tool, {reason} — choose a SPEC and Apply to update it";
        public static string EditingViaPattern(string? spec) => $"Editing SPEC {spec} (via pattern)";
        public static string Editing(string? spec) => $"Editing SPEC {spec}";
        public const string NewBead = "New bead";

        public const string ViaPatternSuffix = " (via pattern)";
        public static string Unnamed(string type, string journalId) => $"{type} {journalId}";
        public static string MoreCurves(int count) => $" +{count} curve(s)";
        public static string FromSketch(string label, string sketch) => $"{label} (from {sketch})";
        public static string Disambiguated(string label, string journalId) => $"{label} [{journalId}]";

        /// <summary>Why a selected unstamped bead has no SPEC when it is not one of the body's beads at all.</summary>
        public const string NotAmongBodyBeads = "it is not among the beads read from this part's body";

        public static string MixedWithNewCurves(string bead, int newCurves) =>
            $"mixes curves of bead {bead} with {newCurves} curve(s) of no bead — select one bead's curves, or only new curves";
        public static string SpansBeads(IEnumerable<string> beads) =>
            $"its curves belong to beads {string.Join(", ", beads)} — select one bead's curves";
    }

    // ---- The preferences label (label_currentPref) ----

    public static class Preferences
    {
        public static string Summary(string state, string thickness, string? grade, string? standard) =>
            $"Sheet Metal Preferences: {state}, t {thickness}" + (grade is null ? "" : $", grade {grade} (Standard {standard})");

        public static string Unreadable(string state) => $"Sheet Metal Preferences: {state}";

        public static string NotUsable(string summary, string problem) => $"{summary}{Environment.NewLine}Not usable: {problem} — fix them in NX";
    }

    // ---- Why no SPEC can be offered ----

    public static class NoSpecs
    {
        public const string NoBeadSpecSelected = "no bead SPEC is selected";
        public static string NoWorkbookNamed(string folder, string beadSpec) => $"no workbook in '{folder}' is named for bead SPEC '{beadSpec}'";
        public static string WorkbookListsNoSpecs(string beadSpec) => $"bead SPEC workbook:'{beadSpec}' lists no SPECs";
        public static string WorkbookUnreadable(string detail) => $"its bead SPEC workbook could not be read: {detail}";
    }

    // ---- Errors (shown in a message box, mostly when Apply is blocked) ----

    public static class Errors
    {
        public static string StandardsUnlistable(string detail) =>
            $"Could not list the Standards in the sheet metal material standards file: {detail}";

        public static string NotExactlyOneBody(int count) => $"This part has {count} sheet metal bodies; the Bead tool needs exactly one.";
        public static string BodyUnreadable(string body) => $"Could not read the sheet metal of body '{body}'.";

        public static string PreferencesUnusable(string problem) =>
            $"This part's Sheet Metal Preferences cannot be used: {problem}. " +
            "Set them to a Material Table entry from the sheet metal material standards file in NX, then reopen the Bead dialog.";

        public static string NoSpecAllowedUnderStandard(string standard, string reason) => $"No bead SPEC is allowed under Standard '{standard}': {reason}.";
        public static string CannotBeOneBead(string target, string? error) => $"'{target}' cannot be one bead: {error}.";

        /// <summary>A SPEC that does not validate, with what would.</summary>
        public static string WithAlternatives(string? blockingMessage, IReadOnlyList<string> alternatives) =>
            blockingMessage + (alternatives.Count > 0
                ? $" Valid alternatives under this bead SPEC: {string.Join(", ", alternatives)}."
                : " No SPECs under this bead SPEC currently validate against this sheet metal.");

        private const string RefusalFix = "Select those beads so Apply rebuilds them to the chosen SPEC, or change the Sheet Metal Preferences in NX.";

        public static string UncoveredItem(string spec, string bead, string coverage) => $"SPEC {spec} (bead '{bead}') covers {coverage}";
        public static string ThicknessNotCovered(string thickness, IEnumerable<string> items) =>
            $"This sheet is {thickness} in thick, which the bead(s) already on this body are not driven for: {string.Join(", ", items)}. {RefusalFix}";

        public static string RefusingItem(string bead, string spec) => $"'{bead}' (SPEC {spec})";
        public static string GradeRefusedByBeads(string row, string grade, string thickness, IEnumerable<string> items) =>
            $"Sheet metal material '{row}' (grade '{grade}') from the Sheet Metal Preferences is not allowed at {thickness} in by " +
            $"the bead(s) already on this body: {string.Join(", ", items)}. {RefusalFix}";

        public const string ChooseSpecFirst = "Choose a SPEC from the bead options first.";
        public const string NothingSelected = "Nothing is selected to build. Select curves or a sketch for new beads, or existing bead feature(s).";
        public const string BuildFailed = "One or more beads failed — see the listing window for details. No changes were committed.";
        public static string RequirementWriteFailed(string? detail) =>
            $"The beads were built, but their requirement could not be written, so nothing was committed: {detail}";
    }

    // ---- Information ----

    public static class Info
    {
        public static string NoSpecs(string beadSpec, string reason) => $"Bead SPEC '{beadSpec}' offers no SPECs: {reason}.";

        public static string NoRowMatches(string beadSpec, string thickness, string? grade, IEnumerable<string> covered) =>
            $"No row of bead SPEC '{beadSpec}' matches the Sheet Metal Preferences: thickness {thickness} in, grade '{grade}'." +
            $"{Para}Thicknesses this bead SPEC covers: {string.Join(", ", covered)} in." +
            $"{Para}Change the Sheet Metal Preferences in NX, or choose another bead SPEC.";

        public const string Applied = "Bead(s) created/updated.";
        public static string AppliedWithNotes(IEnumerable<string> notes) => $"{Applied}{Para}{string.Join(Environment.NewLine, notes)}";

        public const string NoBeadSpec = "(none)";
    }
}
