using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;

namespace Tokonomy.Core.Tests;

public class PlanQualityTests
{
    private static readonly HashSet<string> Repo = new(StringComparer.Ordinal)
    {
        "src/Services/ProductService.cs", "src/Services/IProductService.cs", "src/Program.cs", "tests/ProductTests.cs", "README.md",
    };

    private static WorkUnit Unit(string id, string[]? modify = null, string[]? create = null, string[]? deps = null, string[]? reqs = null,
        string[]? decisions = null, string[]? steps = null, string[]? acceptance = null, string[]? avoid = null, string[]? references = null) => new()
    {
        Id = id,
        Title = id,
        Spec = "Implement " + id,
        Objective = "Do " + id,
        DependsOn = deps ?? [],
        Modify = modify ?? [],
        Create = create ?? [],
        Avoid = avoid ?? [],
        References = references ?? [],
        Steps = steps ?? ["Read the service", "Write the code"],
        Requirements = reqs ?? ["REQ-1"],
        Decisions = decisions ?? [],
        Acceptance = acceptance ?? ["search returns matching products"],
    };

    private static Contract Blueprint(params WorkUnit[] units) => new()
    {
        Task = "add product search",
        SchemaVersion = 2,
        Analysis = new PlanAnalysis
        {
            Facts = [new RepoFact("ProductService owns product queries", ["src/Services/ProductService.cs"])],
            Requirements = [new Requirement("REQ-1", "search by name")],
        },
        Architecture = new ArchitectureDesign { Summary = "Add a search method to the existing service." },
        Decisions = [new ArchitectureDecision { Id = "ADR-1", Title = "Reuse the service", Decision = "Extend ProductService" }],
        Units = units,
    };

    [Fact]
    public void A_complete_grounded_blueprint_is_ready()
    {
        var plan = Blueprint(Unit("U1", modify: ["src/Services/ProductService.cs"], decisions: ["ADR-1"], references: ["src/Services/IProductService.cs:IProductService"]));

        var assessment = PlanQuality.Assess(plan, Repo, requireBlueprint: true);

        Assert.Equal(PlanReadiness.Ready, assessment.Readiness);
        Assert.Empty(assessment.Blocking);
    }

    [Fact]
    public void Valid_json_alone_does_not_make_a_plan_ready_when_a_blueprint_was_asked_for()
    {
        var shortContract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "a", Spec = "b" }] };

        Assert.Equal(PlanReadiness.Ready, PlanQuality.Assess(shortContract).Readiness); // legacy plans stay executable
        var assessment = PlanQuality.Assess(shortContract, Repo, requireBlueprint: true);
        Assert.Equal(PlanReadiness.Incomplete, assessment.Readiness);
        Assert.Contains(assessment.Issues, i => i.Code == "not_blueprint");
    }

    [Fact]
    public void A_broken_dag_is_structurally_invalid()
    {
        var plan = Blueprint(Unit("U1", deps: ["U2"]), Unit("U2", deps: ["U1"]));

        Assert.Equal(PlanReadiness.StructurallyInvalid, PlanQuality.Assess(plan, Repo).Readiness);
    }

    [Fact]
    public void Modifying_a_file_that_does_not_exist_is_ungrounded_unless_an_earlier_unit_creates_it()
    {
        var invented = Blueprint(Unit("U1", modify: ["src/Services/SearchService.cs"]));
        var created = Blueprint(Unit("U1", create: ["src/Services/SearchService.cs"]), Unit("U2", modify: ["src/Services/SearchService.cs"], deps: ["U1"]));

        var a = PlanQuality.Assess(invented, Repo);
        Assert.Equal(PlanReadiness.Incomplete, a.Readiness);
        Assert.Contains(a.Issues, i => i.Code == "ungrounded_modify" && i.UnitId == "U1");
        Assert.True(PlanQuality.Assess(created, Repo).IsReady);
    }

    [Fact]
    public void Facts_and_references_must_cite_real_files()
    {
        var plan = Blueprint(Unit("U1", modify: ["src/Program.cs"], references: ["src/Nope.cs"])) with
        {
            Analysis = new PlanAnalysis
            {
                Facts = [new RepoFact("There is a repository layer", ["src/Data/Repository.cs"])],
                Requirements = [new Requirement("REQ-1", "x")],
            },
        };

        var codes = PlanQuality.Assess(plan, Repo).Issues.Select(i => i.Code).ToList();

        Assert.Contains("ungrounded_fact", codes);
        Assert.Contains("ungrounded_reference", codes);
    }

    [Fact]
    public void Every_requirement_must_be_covered()
    {
        var plan = Blueprint(Unit("U1", modify: ["src/Program.cs"])) with
        {
            Analysis = new PlanAnalysis { Requirements = [new Requirement("REQ-1", "search"), new Requirement("REQ-2", "paginate")] },
        };

        var assessment = PlanQuality.Assess(plan, Repo);

        Assert.Equal(PlanReadiness.Incomplete, assessment.Readiness);
        Assert.Contains(assessment.Issues, i => i.Code == "uncovered_requirement" && i.Message.Contains("REQ-2", StringComparison.Ordinal));
    }

    [Fact]
    public void Units_without_steps_or_acceptance_are_incomplete()
    {
        var plan = Blueprint(Unit("U1", modify: ["src/Program.cs"], steps: [], acceptance: []));

        var codes = PlanQuality.Assess(plan, Repo).Blocking.Select(i => i.Code).ToList();

        Assert.Contains("no_steps", codes);
        Assert.Contains("no_acceptance", codes);
    }

    [Theory]
    [InlineData("unknown_decision")]
    [InlineData("protected_path")]
    [InlineData("avoid_conflict")]
    [InlineData("created_twice")]
    public void Contradictions_are_architecturally_inconsistent(string code)
    {
        var plan = code switch
        {
            "unknown_decision" => Blueprint(Unit("U1", modify: ["src/Program.cs"], decisions: ["ADR-9"])),
            "protected_path" => Blueprint(Unit("U1", modify: [".tokonomy/policy.yaml"])),
            "avoid_conflict" => Blueprint(Unit("U1", modify: ["src/Program.cs"], avoid: ["src/"])),
            _ => Blueprint(Unit("U1", create: ["src/New.cs"]), Unit("U2", create: ["src/New.cs"], deps: ["U1"])),
        };

        var assessment = PlanQuality.Assess(plan, Repo);

        Assert.Contains(assessment.Issues, i => i.Code == code && i.Level == PlanReadiness.ArchitecturallyInconsistent);
        Assert.False(assessment.IsReady);
    }

    [Fact]
    public void Configured_protected_paths_are_checked_too()
    {
        var plan = Blueprint(Unit("U1", modify: ["src/Program.cs", "secrets/keys.json"]));

        Assert.True(PlanQuality.Assess(plan).IsReady);
        Assert.Contains(PlanQuality.Assess(plan, protectedPaths: ["secrets/**"]).Issues, i => i.Code == "protected_path");
    }

    [Fact]
    public void Oversized_units_get_advice_without_blocking()
    {
        var steps = Enumerable.Range(1, 20).Select(i => $"step {i}").ToArray();
        var assessment = PlanQuality.Assess(Blueprint(Unit("U1", modify: ["src/Program.cs"], steps: steps)), Repo);

        Assert.True(assessment.IsReady);
        Assert.Contains(assessment.Issues, i => i.Code == "too_many_steps" && !i.Blocking);
    }

    [Theory]
    [InlineData("./src/a.cs", "src/a.cs")]
    [InlineData("src\\a.cs", "src/a.cs")]
    [InlineData("src/a.cs:Search", "src/a.cs")]
    [InlineData("`src/a.cs`", "src/a.cs")]
    [InlineData("../etc/passwd", null)]
    [InlineData("/etc/passwd", null)]
    [InlineData("C:/Windows/x", null)]
    [InlineData("", null)]
    public void Plan_paths_are_normalized_and_never_escape(string input, string? expected) =>
        Assert.Equal(expected, PlanPaths.Normalize(input));

    [Fact]
    public void Avoid_entries_cover_files_directories_and_globs()
    {
        Assert.True(PlanPaths.Covers("src/Authentication/", "src/Authentication/Jwt.cs"));
        Assert.True(PlanPaths.Covers("src/Authentication", "src/Authentication/Jwt.cs"));
        Assert.True(PlanPaths.Covers("**/*.lock", "a/b/yarn.lock"));
        Assert.False(PlanPaths.Covers("src/Auth", "src/Authentication/Jwt.cs"));
    }
}
