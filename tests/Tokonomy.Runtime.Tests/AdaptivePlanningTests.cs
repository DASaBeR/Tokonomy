using Microsoft.Data.Sqlite;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Supervision;
using Tokonomy.Llm.Architect;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Tests;

public class BlueprintPlannerTests
{
    private static readonly HashSet<string> Repo = new(StringComparer.Ordinal) { "shop/cart.py", "shop/pricing.py", "tests/test_cart.py" };

    internal const string Blueprint = """
        {"schema_version": 2,
         "analysis": {"facts": [{"text": "Cart totals live in shop/cart.py", "evidence": ["shop/cart.py"]}],
                      "assumptions": ["prices are floats"],
                      "requirements": [{"id": "REQ-1", "text": "stack coupons"}]},
         "architecture": {"summary": "Extend Cart.total; keep pricing helpers.", "invariants": ["never below zero"]},
         "decisions": [{"id": "ADR-1", "title": "Reuse apply_coupon", "decision": "Call shop.pricing.apply_coupon", "alternatives_rejected": ["new pricing module"]}],
         "units": [{"id": "U1", "title": "Coupons", "objective": "Accept a list of coupons", "spec": "Change Cart.total",
                    "files": {"modify": ["shop/cart.py"], "avoid": ["shop/storage.py"]}, "references": ["shop/pricing.py"],
                    "steps": ["Read Cart.total", "Accept None, a dict or a list"], "interfaces": ["def total(self, coupons=None) -> float"],
                    "decisions": ["ADR-1"], "edge_cases": ["empty cart"], "requirements": ["REQ-1"], "acceptance": ["100 with [fixed 5, percent 10] is 85.0"],
                    "verification": ["python -m unittest"], "risk": "medium"}],
         "rules": [{"id": "R1", "text": "no new modules"}], "dont": ["do not mutate the coupon list"]}
        """;

    private const string Ungrounded = """
        {"schema_version": 2, "analysis": {"requirements": [{"id": "REQ-1", "text": "x"}]},
         "architecture": {"summary": "s"},
         "units": [{"id": "U1", "title": "t", "spec": "s", "files": {"modify": ["shop/discounts.py"]}, "steps": ["a"],
                    "requirements": ["REQ-1"], "acceptance": ["b"]}]}
        """;

    private const string Ready = """{"verdict": "ready", "summary": "fine", "findings": []}""";

    private const string Revise = """
        {"verdict": "revise", "summary": "gaps", "findings": [{"severity": "high", "category": "edge_case", "unit": "U1", "issue": "negative totals not handled", "fix": "clamp at 0"}]}
        """;

    private static PlanningOptions Options(bool critique = true, int rounds = 1, Func<IReadOnlyList<string>, CancellationToken, Task<string>>? read = null, decimal? cap = null) => new()
    {
        Critique = critique,
        MaxRevisionRounds = rounds,
        RepoFiles = Repo,
        ReadFiles = read,
        SpendCapUsd = cap,
    };

    [Fact]
    public async Task Parses_a_blueprint_with_analysis_architecture_decisions_and_detailed_units()
    {
        var provider = new ScriptedProvider(Blueprint, Ready);

        var result = await new Planner(provider, "strong").PlanAsync("stack coupons", "ctx", default, options: Options());

        var c = result.Contract;
        Assert.Equal(2, c.SchemaVersion);
        Assert.Equal("shop/cart.py", Assert.Single(c.Analysis!.Facts).Evidence.Single());
        Assert.Equal(["prices are floats"], c.Analysis.Assumptions);
        Assert.Equal("ADR-1", Assert.Single(c.Decisions).Id);
        var unit = Assert.Single(c.Units);
        Assert.Equal(["shop/cart.py"], unit.Modify);
        Assert.Equal(["shop/storage.py"], unit.Avoid);
        Assert.Equal(["shop/cart.py"], unit.Files);
        Assert.Equal(2, unit.Steps.Count);
        Assert.Equal(UnitRisk.Medium, unit.Risk);
        Assert.Equal(PlanReadiness.Ready, result.Assessment.Readiness);
        Assert.Equal([PlanPurposes.Planning, PlanPurposes.Critique], result.Purposes);
        Assert.Contains("engineering", provider.Requests[0].System, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PlanCritic.System, provider.Requests[1].System);
    }

    [Fact]
    public async Task An_ungrounded_plan_is_revised_with_the_deterministic_findings()
    {
        var provider = new ScriptedProvider(Ungrounded, Blueprint, Ready);

        var result = await new Planner(provider, "strong").PlanAsync("t", "ctx", default, options: Options());

        Assert.Equal(1, result.RevisionRounds);
        Assert.Contains("shop/discounts.py", provider.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("does not exist in the repository", provider.Requests[1].User, StringComparison.Ordinal);
        Assert.Equal(["shop/cart.py"], result.Contract.Units[0].Modify);
        Assert.True(result.Assessment.IsReady);
        Assert.Equal([PlanPurposes.Planning, PlanPurposes.Revision, PlanPurposes.Critique], result.Purposes);
    }

    [Fact]
    public async Task A_critique_that_finds_real_gaps_leads_to_one_revision()
    {
        var provider = new ScriptedProvider(Blueprint, Revise, Blueprint);

        var result = await new Planner(provider, "strong").PlanAsync("t", "ctx", default, options: Options());

        Assert.Equal(3, provider.Requests.Count);
        Assert.Contains("negative totals not handled", provider.Requests[2].User, StringComparison.Ordinal);
        Assert.Equal(1, result.RevisionRounds);
        Assert.True(result.Assessment.IsReady); // the critique was about the plan before the revision
    }

    [Fact]
    public async Task Without_revision_rounds_left_the_critique_findings_lower_the_readiness()
    {
        var provider = new ScriptedProvider(Blueprint, Revise);

        var result = await new Planner(provider, "strong").PlanAsync("t", "ctx", default, options: Options(rounds: 0));

        Assert.Equal(2, provider.Requests.Count);
        Assert.False(result.Assessment.IsReady);
        Assert.Contains(result.Assessment.Issues, i => i.Code == "critique_edge_case" && i.Blocking);
    }

    [Fact]
    public async Task A_revision_that_breaks_the_structure_is_discarded()
    {
        var provider = new ScriptedProvider(Ungrounded, "not json at all", Ready);

        var result = await new Planner(provider, "strong").PlanAsync("t", "ctx", default, options: Options(critique: false));

        Assert.Equal(["shop/discounts.py"], result.Contract.Units[0].Modify);
        Assert.Equal(PlanReadiness.Incomplete, result.Assessment.Readiness);
    }

    [Fact]
    public async Task An_unreadable_critique_never_blocks_the_plan()
    {
        var provider = new ScriptedProvider(Blueprint, "I think it is fine.");

        var result = await new Planner(provider, "strong").PlanAsync("t", "ctx", default, options: Options());

        Assert.Equal(CritiqueVerdict.Unavailable, result.Critique!.Verdict);
        Assert.True(result.Assessment.IsReady);
    }

    [Fact]
    public async Task The_architect_may_read_files_once_before_planning()
    {
        var provider = new ScriptedProvider("""{"read_files": ["shop/cart.py", "../../etc/passwd"]}""", """{"read_files": ["x"]}""", Blueprint, Ready);
        IReadOnlyList<string>? asked = null;

        var result = await new Planner(provider, "strong").PlanAsync("t", "ctx", default, options: Options(read: (paths, _) =>
        {
            asked = paths;
            return Task.FromResult("### shop/cart.py\nclass Cart: ...");
        }));

        Assert.Equal(["shop/cart.py"], asked); // escaping paths are dropped before they reach the reader
        Assert.Equal(["shop/cart.py"], result.InspectedFiles);
        Assert.Contains("class Cart", provider.Requests[1].User, StringComparison.Ordinal);
        Assert.Equal(PlanPurposes.Inspection, result.Purposes[0]);
        // A second read request is not honoured: it counts as an invalid answer, then the plan arrives.
        Assert.Equal(2, result.Contract.Units[0].Steps.Count);
    }

    [Fact]
    public async Task The_spend_cap_stops_extra_rounds()
    {
        var provider = new ScriptedProvider(Ungrounded, Blueprint, Ready);

        var result = await new Planner(provider, "strong").PlanAsync("t", "ctx", default, options: Options(cap: 0.005m));

        Assert.Single(provider.Requests);
        Assert.Equal(0, result.RevisionRounds);
        Assert.Null(result.Critique);
    }

    [Fact]
    public void Critique_parsing_normalizes_severity_and_reads_findings()
    {
        var critique = PlanCritic.Parse("""{"verdict":"Revise","findings":[{"severity":"Critical","category":"Coverage","unit":"null","issue":"REQ-2 missing"}]}""");

        Assert.Equal(CritiqueVerdict.Revise, critique.Verdict);
        var finding = Assert.Single(critique.Findings);
        Assert.Equal("high", finding.Severity);
        Assert.Null(finding.Unit);
        Assert.False(critique.Accepts);
    }
}

public class SupervisionPromptTests
{
    private static readonly IReadOnlyList<InterventionLevel> All =
        [InterventionLevel.None, InterventionLevel.InstructionCorrection, InterventionLevel.GuidedCorrection, InterventionLevel.Takeover];

    [Fact]
    public void Parses_the_decision_plan_updates_lessons_and_edits()
    {
        var reply = SupervisorPrompt.Parse("""
            ```json
            {"level": 2, "root_cause": "plan error", "diagnosis": "IProductService does not exist; ProductQueries does.",
             "instructions": "Use ProductQueries.", "plan_updates": [{"op": "update_unit", "unit": "U3", "constraints": ["use ProductQueries"]}],
             "lessons": [{"text": "There is no IProductService", "scope": "global", "forbidden_pattern": "IProductService"}],
             "summary": "switched to ProductQueries", "why": "the plan named a missing interface"}
            ```
            <<<<<<< SEARCH src/search.py
            from services import IProductService
            =======
            from queries import ProductQueries
            >>>>>>> REPLACE
            """, All, "U2");

        Assert.True(reply.Usable, string.Join("; ", reply.Problems));
        Assert.Equal(InterventionLevel.GuidedCorrection, reply.Level);
        Assert.Equal("plan_error", reply.RootCause);
        Assert.Equal("U3", Assert.Single(reply.PlanUpdates).Unit);
        var lesson = Assert.Single(reply.Lessons);
        Assert.Equal(PlanOps.AddLesson, lesson.Op);
        Assert.Equal("U2", lesson.Unit);
        Assert.Equal("src/search.py", Assert.Single(reply.Edits).Path);
    }

    [Theory]
    [InlineData("""{"level": 3, "diagnosis": "x"}""", "not allowed")]
    [InlineData("""{"level": 1, "diagnosis": "x"}""", "needs instructions")]
    [InlineData("""{"level": "takeover", "diagnosis": "x"}""", "not allowed")]
    [InlineData("""{"level": 9}""", "unknown level")]
    [InlineData("no json", "no readable JSON")]
    public void Unusable_decisions_are_reported_not_acted_on(string text, string problem)
    {
        var reply = SupervisorPrompt.Parse(text, [InterventionLevel.None, InterventionLevel.InstructionCorrection], "U1");

        Assert.False(reply.Usable);
        Assert.Contains(reply.Problems, p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void A_correction_without_edits_is_unusable() =>
        Assert.Contains("needs edit blocks", SupervisorPrompt.Parse("""{"level": 2, "diagnosis": "x"}""", All, "U1").Problems.Single(), StringComparison.Ordinal);

    [Fact]
    public void A_human_decision_is_always_honoured() =>
        Assert.Equal("approve the new package?", SupervisorPrompt.Parse("""{"level": 3, "human_decision": "approve the new package?"}""", [InterventionLevel.None], "U1").HumanDecision);

    [Fact]
    public void The_prompt_carries_the_report_the_deviations_and_the_remaining_plan()
    {
        var plan = new Contract
        {
            Task = "search",
            SchemaVersion = 2,
            Units = [new WorkUnit { Id = "U1", Title = "Query", Spec = "s", Steps = ["write query"] }, new WorkUnit { Id = "U2", Title = "API", Spec = "s", DependsOn = ["U1"] }],
            Decisions = [new ArchitectureDecision { Id = "ADR-1", Title = "t", Decision = "filter in SQL" }],
        };
        var prompt = SupervisorPrompt.Build(new SupervisionInput
        {
            Plan = plan,
            Revision = 3,
            Unit = plan.Units[0],
            Request = new SupervisionRequest(SupervisionTrigger.WorkerBlocked, "the worker reported Blocked", All),
            Attempts = 1,
            Report = new WorkerReport { Status = WorkerReportStatus.Blocked, Reason = "missing_interface", Description = "no repo class", Problems = ["evidence 'x' does not exist in the worktree"] },
            Deviations = ["changed path(s) the unit must avoid: auth.py"],
            Remaining = [plan.Units[1]],
        });

        Assert.Contains("plan revision 3", prompt, StringComparison.Ordinal);
        Assert.Contains("missing_interface", prompt, StringComparison.Ordinal);
        Assert.Contains("does not exist in the worktree", prompt, StringComparison.Ordinal);
        Assert.Contains("auth.py", prompt, StringComparison.Ordinal);
        Assert.Contains("U2: API", prompt, StringComparison.Ordinal);
        Assert.Contains("filter in SQL", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not assume your plan is right", SupervisorPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_replies_that_contradict_themselves_trust_the_findings()
    {
        var reply = UnitReviewPrompt.Parse("""
            {"verdict": "approve", "summary": "ok", "findings": [{"severity": "high", "category": "architectural_violation", "file": "a.py", "description": "bypasses the repository"}]}
            """, "strong", "U1");

        Assert.Equal(UnitReviewVerdict.RequestChanges, reply.Review.Verdict);
        Assert.Equal(UnitReviewVerdict.ApproveWithWarnings, UnitReviewPrompt.Parse("""{"verdict":"approve","findings":[{"severity":"low","description":"naming"}]}""", "m", "U1").Review.Verdict);
        Assert.Equal(UnitReviewVerdict.Unavailable, UnitReviewPrompt.Parse("LGTM", "m", "U1").Review.Verdict);
    }

    [Fact]
    public void Decomposition_replies_become_units()
    {
        var (parts, reason) = DecomposePrompt.Parse("""{"reason": "two concerns", "parts": [{"id": "U1a", "title": "a", "spec": "a", "steps": ["x"]}, {"id": "U1b", "title": "b", "spec": "b"}]}""");

        Assert.Equal(["U1a", "U1b"], parts.Select(p => p.Id));
        Assert.Equal("two concerns", reason);
        Assert.Empty(DecomposePrompt.Parse("nope").Parts);
    }
}

public class RepositoryAnalyzerTests
{
    [Fact]
    public async Task Builds_an_inventory_and_includes_the_files_relevant_to_the_task()
    {
        using var repo = new TestRepo(
            ("shop/cart.py", "class Cart:\n    def total(self, coupon=None):\n        return 0\n"),
            ("shop/storage.py", "def save(): pass\n"),
            ("tests/test_cart.py", "import unittest\n"),
            ("pyproject.toml", "[project]\nname='shop'\n"),
            ("README.md", "# Shop\n"));
        repo.Write("scratch.txt", "uncommitted");

        var inventory = await RepositoryAnalyzer.AnalyzeAsync(repo.Path, repo.Head, "Let Cart.total accept several coupons", new GitCli(new ProcessRunner()), default);

        Assert.Contains("Python", inventory.Context, StringComparison.Ordinal);
        Assert.Contains("pyproject.toml", inventory.Context, StringComparison.Ordinal);
        Assert.Contains("tests/", inventory.Context, StringComparison.Ordinal);
        Assert.Contains("scratch.txt", inventory.Context, StringComparison.Ordinal); // working-copy changes are flagged
        Assert.Equal("shop/cart.py", inventory.RelevantFiles[0]);
        Assert.Contains("def total(self, coupon=None)", inventory.Context, StringComparison.Ordinal);
        Assert.Contains("shop/storage.py", inventory.Files);
        Assert.DoesNotContain("scratch.txt", inventory.Files);
    }

    [Fact]
    public async Task Reading_files_serves_only_tracked_files()
    {
        using var repo = new TestRepo(("a.py", "print('a')\n"));
        repo.Write("untracked.py", "secret");
        var tracked = new HashSet<string>(StringComparer.Ordinal) { "a.py" };

        var text = await RepositoryAnalyzer.ReadFilesAsync(repo.Path, repo.Head, tracked, ["a.py", "untracked.py", "missing.py"], new GitCli(new ProcessRunner()), default);

        Assert.Contains("print('a')", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.Contains("not a tracked file", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_terms_split_identifiers_and_drop_noise() =>
        Assert.Equal(["productsearchservice", "product", "search", "service", "pagination"],
            RepositoryAnalyzer.Terms("Add the ProductSearchService with pagination"));
}

public class BriefCompilerTests
{
    private static readonly Contract Plan = new()
    {
        Task = "Add product search",
        SchemaVersion = 2,
        Architecture = new ArchitectureDesign { Summary = "Search lives in the existing service." },
        Decisions =
        [
            new ArchitectureDecision { Id = "ADR-1", Title = "Reuse the ORM", Decision = "Use the existing repository" },
            new ArchitectureDecision { Id = "ADR-2", Title = "Unrelated", Decision = "Something else" },
        ],
        Units =
        [
            new WorkUnit
            {
                Id = "U3", Title = "Search service", Spec = "Implement search", Objective = "Database-backed search",
                Modify = ["src/search.py"], Avoid = ["src/auth/"], Steps = ["Inspect the repository", "Filter before materializing"],
                Interfaces = ["def search(q: str, page: int) -> list[Product]"], Decisions = ["ADR-1"], Constraints = ["no new ORM"],
                EdgeCases = ["empty query"], Acceptance = ["matching products returned"], Verification = ["python -m unittest"],
                Corrections = ["Use ProductRepository.query, not raw SQL"],
            },
        ],
        DontList = ["load the whole catalog"],
    };

    [Fact]
    public void The_brief_is_an_operational_instruction_package_for_the_current_revision()
    {
        var brief = BriefWriter.Compile(new BriefInput
        {
            Plan = Plan,
            Unit = Plan.Units[0],
            Attempt = 2,
            BriefFileName = "AGENTS.md",
            Revision = 4,
            Lessons = [new Lesson { Id = "L1", Text = "filter in SQL", ForbiddenPattern = "ToList" }],
            RequestReport = true,
            Feedback = "gate failed",
        }).Body;

        foreach (var expected in new[]
                 {
                     "You MUST NOT", "Plan revision 4", "Search lives in the existing service.", "ADR-1 Reuse the ORM", "Objective: Database-backed search",
                     "Files to modify: src/search.py", "Do not touch: src/auth/", "1. Inspect the repository", "2. Filter before materializing",
                     "def search(q: str, page: int)", "no new ORM", "empty query", "python -m unittest", "load the whole catalog",
                     "L1: filter in SQL (enforced by an automated check)", "Use ProductRepository.query", WorkerReports.FileName, "Previous attempt failed",
                 })
        {
            Assert.Contains(expected, brief, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("ADR-2", brief, StringComparison.Ordinal); // only the decisions that bind the unit
    }

    [Fact]
    public void Without_adaptive_supervision_the_brief_does_not_ask_for_a_report()
    {
        var brief = BriefWriter.Build(Plan, Plan.Units[0], 1, "AGENTS.md", null, []).Body;

        Assert.DoesNotContain(WorkerReports.FileName, brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Plan revision", brief, StringComparison.Ordinal);
        Assert.Contains("1. Inspect the repository", brief, StringComparison.Ordinal);
    }
}

public class AdaptiveLedgerTests
{
    [Fact]
    public void An_existing_version_2_ledger_is_migrated_and_keeps_its_rows()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "ledger.db");
        var migrations = (string[])typeof(SqliteLedger).GetField("Migrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = migrations[0] + migrations[1] + """
                PRAGMA user_version = 2;
                INSERT INTO runs(id, strategy, task_id, repo, status, started) VALUES('old', '4', 't', 'r', 'Pass', '2026-11-01T00:00:00Z');
                INSERT INTO llm_calls(run_id, role, model, input_tokens, output_tokens, cached_tokens, usd_micros, source) VALUES('old', 'Architect', 'opus', 10, 5, 0, 20000, 'Metered');
                """;
            cmd.ExecuteNonQuery();
        }

        using var ledger = new SqliteLedger(path);

        Assert.Equal(3, ledger.SchemaVersion);
        var summary = ledger.GetSummary("old")!;
        Assert.Equal(0.02m, summary.Tiers.Single().Usd);
        Assert.Equal("unspecified", summary.Purposes.Single().Purpose);
    }

    [Fact]
    public void Interventions_revisions_reviews_and_purposes_are_recorded_and_summarized()
    {
        using var tmp = new TempDir();
        using var ledger = new SqliteLedger(Path.Combine(tmp.Path, "ledger.db"));
        ledger.StartRun(new RunRecord { Id = "r", Strategy = "5", TaskId = "t", Repo = "x", Started = DateTimeOffset.UtcNow });
        ledger.RecordLlmCall(new LlmCallRecord { RunId = "r", Role = LlmRole.Architect, Model = "opus", Usd = 0.10m, Purpose = PlanPurposes.Planning });
        ledger.RecordLlmCall(new LlmCallRecord { RunId = "r", Role = LlmRole.Architect, Model = "opus", Usd = 0.03m, Purpose = CallPurposes.Supervision });
        ledger.RecordLlmCall(new LlmCallRecord { RunId = "r", Role = LlmRole.Worker, Model = "flash", Usd = 0.01m });
        ledger.UpsertUnit("r", "U1", "a", "accepted", 3, 1);
        ledger.UpsertUnit("r", "U2", "b", "accepted", 1, 0);
        ledger.RecordAttempt(new AttemptRecord { RunId = "r", UnitId = "U1", Number = 1, Executor = "fake", Decision = DecisionKind.Retry, Reason = "x" });
        ledger.RecordAttempt(new AttemptRecord { RunId = "r", UnitId = "U1", Number = 2, Executor = "fake", Decision = DecisionKind.Takeover, Reason = "x" });
        ledger.RecordAttempt(new AttemptRecord { RunId = "r", UnitId = "U1", Number = 3, Executor = "architect", Decision = DecisionKind.Accept, Reason = "takeover: gates pass" });
        var lessons = ledger.RecordAttempt(new AttemptRecord { RunId = "r", UnitId = "U2", Number = 1, Executor = "fake", Decision = DecisionKind.Retry, Reason = "x" });
        ledger.RecordGateResult(lessons, new GateResult { Gate = LessonCheck.GateName, Status = GateStatus.Failed }, null);
        ledger.RecordIntervention(new InterventionRecord { RunId = "r", UnitId = "U1", At = DateTimeOffset.UtcNow, Trigger = "worker_blocked", Level = 1, Outcome = "resumed", PlanRevision = 2, Evidence = ["a.py"], Usd = 0.03m });
        ledger.RecordPlanRevision("r", new PlanRevision { Number = 2, At = DateTimeOffset.UtcNow, Source = "supervision", Reason = "fix", Changes = [new PlanChange("update_unit", "U2", "constraints", "[]", "[\"x\"]")] });
        ledger.RecordUnitReview(new UnitReviewRecord("r", "U2", DateTimeOffset.UtcNow, UnitReviewVerdict.RequestChanges, "no", "high-risk unit",
            [new ReviewFinding("high", "architectural_violation", "a.py", "bypasses the repository")]));

        var summary = ledger.GetSummary("r")!;

        Assert.Equal(0.03m, summary.Purposes.Single(p => p.Purpose == CallPurposes.Supervision).Usd);
        Assert.Equal("work", summary.Purposes.Single(p => p.Role == LlmRole.Worker).Purpose);
        var s = summary.Stats;
        Assert.Equal(2, s.UnitsAccepted);
        Assert.Equal(1, s.UnitsAcceptedWithoutTakeover);
        Assert.Equal(3, s.WorkerAttempts);
        Assert.Equal(1, s.WorkerRetries);
        Assert.Equal(1, s.Takeovers);
        Assert.Equal(1, s.Interventions);
        Assert.Equal(1, s.InstructionCorrections);
        Assert.Equal(1, s.PlanRevisions);
        Assert.Equal(1, s.ReviewChangeRequests);
        Assert.Equal(1, s.ArchitecturalViolations);
        Assert.Equal(1, s.RepeatedMistakes);
        Assert.Equal(["a.py"], summary.Interventions.Single().Evidence);
        Assert.Equal(1, summary.Revisions.Single().Changes);
        Assert.Equal("bypasses the repository", summary.Reviews.Single().Findings.Single().Description);
    }

    [Fact]
    public void Plan_state_is_saved_atomically_and_loads_back()
    {
        using var tmp = new TempDir();
        var artifacts = new RunArtifacts(tmp.Path, "run-1");
        var plan = new PlanManager(new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "a", Spec = "b" }] });
        plan.Apply(new PlanUpdate { BaseRevision = 1, Source = "supervision", Reason = "r", Operations = [new PlanOperation { Op = PlanOps.AddDont, Text = "x" }] });

        artifacts.SavePlanState(plan.Snapshot());
        var loaded = artifacts.LoadPlanState()!;

        Assert.Equal(2, loaded.Revision);
        Assert.Equal(["x"], loaded.Current.DontList);
        Assert.Empty(loaded.Original.DontList);
        Assert.False(File.Exists(artifacts.PlanStatePath + ".tmp"));
    }

    [Fact]
    public void Plans_saved_by_older_builds_still_load()
    {
        using var tmp = new TempDir();
        var artifacts = new RunArtifacts(tmp.Path, "old");
        Directory.CreateDirectory(artifacts.Directory);
        File.WriteAllText(artifacts.PlanPath, """
            {"run_id": "old", "task": "t", "strategy": "4", "base_sha": "abc", "created": "2026-11-01T00:00:00+00:00",
             "contract": {"task": "t", "units": [{"id": "U1", "title": "a", "spec": "b", "depends_on": [], "files": ["x.py"], "acceptance": [], "tests": []}],
                          "rules": [], "dont_list": []}}
            """);

        var plan = RunArtifacts.LoadPlan(artifacts.PlanPath);

        Assert.Equal(1, plan.Contract.SchemaVersion);
        Assert.Null(plan.Readiness);
        Assert.Equal(["x.py"], plan.Contract.Units[0].ExpectedFiles);
    }
}

public class AdaptiveConfigTests
{
    [Theory]
    [InlineData("review.mode")]
    [InlineData("review.on_unavailable")]
    [InlineData("planning.max_revision_rounds")]
    [InlineData("supervision.max_interventions_per_unit")]
    public void Invalid_new_sections_are_reported(string key)
    {
        var policy = new Policy();
        switch (key)
        {
            case "review.mode":
                policy.Review.Mode = "sometimes";
                break;
            case "review.on_unavailable":
                policy.Review.OnUnavailable = "panic";
                break;
            case "planning.max_revision_rounds":
                policy.Planning.MaxRevisionRounds = 9;
                break;
            default:
                policy.Supervision.MaxInterventionsPerUnit = -1;
                break;
        }

        Assert.Contains(ConfigLoader.Validate(policy, "policy.yaml"), i => i.Severity == Severity.Error && i.Message.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void Defaults_are_valid() =>
        Assert.DoesNotContain(ConfigLoader.Validate(new Policy(), "policy.yaml"), i => i.Message.Contains("review.", StringComparison.Ordinal) ||
                                                                                       i.Message.Contains("planning.", StringComparison.Ordinal) ||
                                                                                       i.Message.Contains("supervision.", StringComparison.Ordinal));
}
