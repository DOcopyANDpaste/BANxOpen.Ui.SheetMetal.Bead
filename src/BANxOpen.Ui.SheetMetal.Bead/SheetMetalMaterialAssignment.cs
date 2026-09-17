using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Foundation.Contracts.Materials;
using BANxOpen.Foundation.Core.Materials.Assignment;
using BANxOpen.Foundation.Core.Materials.Bodies;
using BANxOpen.Foundation.Core.Materials.Library;
using BANxOpen.Foundation.Core.Materials.Rules;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.Foundation.NxAdapters.Materials;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>The bead dialog's material assignment, done through the shared material engine rather than a
/// private copy of it. The verdict comes from the same rule modules as the Material Assignment dialog, including the
/// bead SPEC constraints, so the two dialogs cannot disagree about what may go on a body — and a material assigned
/// here gets the same display material as one assigned there.
///
/// The bead dialog never offers a list of materials: the user picks a row of the sheet metal material standards
/// file, and the row names its physical material (<see cref="FindMaterial"/>).</summary>
public sealed class SheetMetalMaterialAssignment
{
    private readonly NxSessionContext _context;
    private readonly IPartMaterialService _partMaterials;
    private readonly MaterialRuleSet _rules;
    private readonly IMaterialAssignmentPlanner _planner;
    private readonly IAssignmentPlanFinalizer _finalizer;
    private readonly IMaterialLibraryRepository _libraryRepository;
    private readonly IMaterialLibraryLoader _libraryLoader;

    private IReadOnlyList<MaterialLibrary>? _libraries;

    public SheetMetalMaterialAssignment(
        NxSessionContext context,
        MaterialEngine engine,
        IMaterialLibraryRepository libraryRepository,
        IMaterialLibraryLoader libraryLoader)
    {
        _context = context;
        _partMaterials = engine.PartMaterials;
        _rules = engine.Rules;
        _planner = engine.Rules.CreatePlanner();
        _finalizer = engine.Rules.CreateFinalizer();
        _libraryRepository = libraryRepository;
        _libraryLoader = libraryLoader;
    }

    /// <summary>The library material named <paramref name="physicalMaterialName"/> — a row's PHYSICAL_MATERIAL_NAME.</summary>
    /// <returns>A failure naming the material when no library has it, or when more than one does: assigning an arbitrary
    /// one of two same-named materials could apply the wrong properties.</returns>
    public OperationResult<Material> FindMaterial(string physicalMaterialName)
    {
        var matches = Libraries()
            .SelectMany(library => library.Materials)
            .Where(m => string.Equals(m.Name, physicalMaterialName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => OperationResult<Material>.Success(matches[0]),
            0 => OperationResult<Material>.Fail(
                "MATERIAL_NOT_IN_LIBRARY",
                $"Physical material '{physicalMaterialName}' is not in any material library, so it cannot be assigned."),
            _ => OperationResult<Material>.Fail(
                "MATERIAL_AMBIGUOUS",
                $"Physical material '{physicalMaterialName}' is in more than one material library " +
                $"({string.Join(", ", matches.Select(m => m.LibraryId.Value))}), so which one to assign is unclear."),
        };
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

    private static string Describe(IEnumerable<BANxOpen.Foundation.Core.RuleEngine.RuleOutcome> outcomes, string fallback)
    {
        var messages = outcomes.Select(o => o.Message).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        return messages.Count == 0 ? fallback : string.Join(Environment.NewLine, messages);
    }
}
