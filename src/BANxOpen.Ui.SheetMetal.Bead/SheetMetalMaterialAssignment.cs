using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Foundation.Contracts.Materials;
using BANxOpen.Foundation.Core.Materials.Assignment;
using BANxOpen.Foundation.Core.Materials.Bodies;
using BANxOpen.Foundation.Core.Materials.Library;
using BANxOpen.Foundation.Core.Materials.Rules;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.Foundation.NxAdapters.Materials;
using BANxOpen.SheetMetal.Materials;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>A material the bead dialog can offer, with the text shown for it in the combo.</summary>
public sealed record PickableMaterial(string DisplayText, Material Material);

/// <summary>What can be offered for one body, and any advisories that apply to all of it.</summary>
public sealed record PickableMaterials(IReadOnlyList<PickableMaterial> Materials, IReadOnlyList<string> Warnings)
{
    public static readonly PickableMaterials None = new(Array.Empty<PickableMaterial>(), Array.Empty<string>());
}

/// <summary>The bead dialog's material assignment, done through the shared material engine rather than a
/// private copy of it. The list offered and the verdict on Apply come from the same rule modules as the Material
/// Assignment dialog, including the bead SPEC constraints, so the two dialogs cannot disagree about what may go on
/// a body — and a material assigned here gets the same display material and preference sync as one assigned
/// there.</summary>
public sealed class SheetMetalMaterialAssignment
{
    private readonly NxSessionContext _context;
    private readonly IPartMaterialService _partMaterials;
    private readonly MaterialRuleSet _rules;
    private readonly IMaterialAssignmentPlanner _planner;
    private readonly IAssignmentPlanFinalizer _finalizer;
    private readonly IMaterialLibraryRepository _libraryRepository;
    private readonly IMaterialLibraryLoader _libraryLoader;
    private readonly MaterialGradeMap _gradeMap;

    private IReadOnlyList<MaterialLibrary>? _libraries;

    public SheetMetalMaterialAssignment(
        NxSessionContext context,
        MaterialEngine engine,
        IMaterialLibraryRepository libraryRepository,
        IMaterialLibraryLoader libraryLoader,
        MaterialGradeMap gradeMap)
    {
        _context = context;
        _partMaterials = engine.PartMaterials;
        _rules = engine.Rules;
        _planner = engine.Rules.CreatePlanner();
        _finalizer = engine.Rules.CreateFinalizer();
        _libraryRepository = libraryRepository;
        _libraryLoader = libraryLoader;
        _gradeMap = gradeMap;
    }

    /// <summary>Materials that may be assigned to the body.</summary>
    /// <param name="allowedGrades">When given, only materials whose grade is in this set are offered — the
    /// grades some SPEC in the chosen Standard allows at the body's thickness. A material with no grade-map
    /// entry is then left out too, since no SPEC could be validated against it afterwards.</param>
    public PickableMaterials ListPickable(BodyId bodyId, IReadOnlyCollection<string>? allowedGrades)
    {
        var body = FindBody(bodyId);
        if (body is null)
            return PickableMaterials.None;

        var candidates = Libraries().SelectMany(library => library.Materials);
        if (allowedGrades is not null)
            candidates = candidates.Where(m => _gradeMap.GradeFor(m.Name) is { } grade && allowedGrades.Contains(grade));

        var assignments = _partMaterials.GetCurrentAssignments();
        assignments.TryGetValue(bodyId, out var current);

        // A fresh caching planner per listing: the query plans this one body against every candidate, and the bead
        // inventory behind the constraints re-reads the model on each call. The cache must not outlive this
        // listing, or it would keep answering from a model the user has since changed.
        var planner = _rules.CreatePlanner(cacheFeatureConstraints: true);

        var results = new AssignableMaterialQuery(planner).Evaluate(body, current, candidates);

        var assignable = results.Where(r => r.IsAssignable).Select(r => r.Material).ToList();
        var warnings = results.SelectMany(r => r.Warnings).Distinct().ToList();

        return new PickableMaterials(Label(assignable), warnings);
    }

    /// <summary>Plans, confirms, and applies one material to one body — the same sequence the Material
    /// Assignment dialog runs, through the same rules.</summary>
    /// <returns>On success, any warnings the rules raised, for the caller to show.</returns>
    public OperationResult<IReadOnlyList<string>> Assign(BodyId bodyId, Material material, Func<string, bool> confirm)
    {
        var body = FindBody(bodyId);
        if (body is null)
            return OperationResult<IReadOnlyList<string>>.Fail("BODY_NOT_FOUND", "The selected body is no longer in the part.");

        // ApplyPlan resolves a material by id against the registered libraries, so they must be loaded first.
        Libraries();

        var input = new MaterialAssignmentPlanningInput(material, new[] { body }, _partMaterials.GetCurrentAssignments());
        var plan = _planner.Plan(input);
        var evaluation = plan.BodyEvaluations.Single();

        if (evaluation.IsBlocked)
        {
            return OperationResult<IReadOnlyList<string>>.Fail(
                "ASSIGNMENT_BLOCKED", Describe(evaluation.BlockingOutcomes, $"'{material.Name}' cannot be assigned to this body."));
        }

        var confirmed = new HashSet<BodyId>();
        if (evaluation.RequiresConfirmation)
        {
            var question = $"{Describe(evaluation.ConfirmationOutcomes, "")}{Environment.NewLine}{Environment.NewLine}Assign '{material.Name}' anyway?";
            if (!confirm(question))
                return OperationResult<IReadOnlyList<string>>.Fail("ASSIGNMENT_DECLINED", "Material assignment cancelled.");

            confirmed.Add(bodyId);
        }

        var executable = _finalizer.Finalize(plan, input, confirmed);
        var applied = _partMaterials.ApplyPlan(executable);
        if (!applied.Ok)
            return OperationResult<IReadOnlyList<string>>.Fail(applied.ErrorCode ?? "APPLY_FAILED", applied.Message ?? "Material assignment failed.");

        var warnings = evaluation.WarningOutcomes
            .Select(o => o.Message)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!)
            .ToList();

        foreach (var warning in warnings)
            _context.Log.Warn(warning);

        return OperationResult<IReadOnlyList<string>>.Success(warnings);
    }

    private BodyInfo? FindBody(BodyId bodyId) => _partMaterials.GetBodies().FirstOrDefault(b => b.Id == bodyId);

    /// <summary>Every available library, loaded once per dialog session and registered for material
    /// resolution. A library that fails to load is skipped with a warning rather than hiding every other
    /// library's materials.</summary>
    private IReadOnlyList<MaterialLibrary> Libraries()
    {
        if (_libraries is not null)
            return _libraries;

        var loaded = new List<MaterialLibrary>();
        foreach (var reference in _libraryRepository.ListAvailableLibraries())
        {
            try
            {
                loaded.Add(_libraryLoader.GetOrLoad(reference));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or System.Xml.XmlException)
            {
                _context.Log.Warn($"Skipped material library '{reference.DisplayName}': {ex.Message}");
            }
        }

        _partMaterials.SetResolutionLibraries(loaded);
        _libraries = loaded;
        return loaded;
    }

    /// <summary>Material names alone, unless two libraries share a name — then the library is appended so the
    /// combo never offers two identical entries that assign different materials.</summary>
    private static IReadOnlyList<PickableMaterial> Label(IReadOnlyList<Material> materials)
    {
        var duplicated = materials.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return materials
            .Select(m => new PickableMaterial(duplicated.Contains(m.Name) ? $"{m.Name} ({m.LibraryId})" : m.Name, m))
            .ToList();
    }

    private static string Describe(IEnumerable<BANxOpen.Foundation.Core.RuleEngine.RuleOutcome> outcomes, string fallback)
    {
        var messages = outcomes.Select(o => o.Message).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        return messages.Count == 0 ? fallback : string.Join(Environment.NewLine, messages);
    }
}
