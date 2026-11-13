#pragma warning disable CA1861 // inline arrays describe scripted architect replies
using System.Text.Json;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Supervision;
using Tokonomy.Llm.Architect;
using Tokonomy.Core.Takeover;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>
/// Strategy 5 end to end with real git, real gates (host-unsafe) and the real ledger; only the worker and the architect
/// are scripted. The base commit passes the gate ("feature" must be in app.txt), so a regression blocks.
/// </summary>
public sealed class AdaptiveOrchestratorTests : IDisposable
{
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"), ("README.md", "# demo\n"));
    private readonly string _config;
    private readonly SqliteLedger _ledger;

    public AdaptiveOrchestratorTests()
    {
        _config = Path.Combine(_repo.Path, ".tokonomy");
        _ledger = new SqliteLedger(Path.Combine(_config, "ledger.db"));
    }

    private RunArtifacts Artifacts => new(_config, "run-1");

    public void Dispose()
    {
        _ledger.Dispose();
        _repo.Dispose();
    }

    private static Policy PolicyWithGate(string review = ReviewConfig.Off) => new()
    {
        Models = { Architect = "strong", Worker = "cheap" },
        GateExecution = new GateExecutionConfig { Provider = GateExecutionConfig.HostUnsafeProvider },
        Gates = [new GateConfig { Name = "has-feature", Cmd = Shell.FileContains("app.txt", "feature") }],
        Review = { Mode = review },
    };

    private static WorkUnit Feature(UnitRisk risk = UnitRisk.Low) => new()
    {
        Id = "U1",
        Title = "Feature",
        Spec = "Change app.txt to feature v2",
        Objective = "app.txt describes feature v2",
        Modify = ["app.txt"],
        Files = ["app.txt"],
        Steps = ["Replace the content of app.txt with 'feature v2'"],
        Acceptance = ["app.txt says feature v2"],
        Risk = risk,
    };

    private static WorkUnit Docs() => new()
    {
        Id = "U2",
        Title = "Docs",
        Spec = "Document the feature in DOCS.md",
        Create = ["DOCS.md"],
        Files = ["DOCS.md"],
        Steps = ["Write DOCS.md"],
        Acceptance = ["DOCS.md exists"],
        DependsOn = ["U1"],
    };

    private static Contract Plan(params WorkUnit[] units) => new() { Task = "Ship feature v2 with docs", SchemaVersion = 2, Units = units };

    private async Task<RunOutcome> Run(Contract contract, Policy policy, FakeExecutor executor, IProvider? architect, Strategy? strategy = null)
    {
        strategy ??= Strategy.Adaptive;
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = strategy.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var orchestrator = new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor, architect: architect);
        return await orchestrator.RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = contract,
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = Artifacts,
            WorkerModel = policy.Models.Worker,
            UseGates = strategy.UseGates,
            AllowEscalation = strategy.AllowEscalation,
            AdaptiveSupervision = strategy.AdaptiveSupervision,
            MaxAttempts = strategy.MaxAttempts,
        }, null, default);
    }

    private static Action<Brief, string> Do(params (string File, string Content)[] writes) => (_, dir) =>
    {
        foreach (var (file, content) in writes)
        {
            var full = Path.Combine(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    };

    private static (string, string) Report(string status, string reason = "", string description = "", params string[] evidence) =>
        (WorkerReports.FileName, JsonSerializer.Serialize(new { status, reason, description, evidence, decision_required = status == "blocked" }));

    /// <summary>A supervision reply: the JSON decision in a fence, then optional edit blocks.</summary>
    private static string Decide(object decision, string edits = "") =>
        "```json\n" + JsonSerializer.Serialize(decision) + "\n```\n" + edits;

    private static string Approve(string summary = "follows the plan") => JsonSerializer.Serialize(new { verdict = "approve", summary, findings = Array.Empty<object>() });

    // ---------------------------------------------------------------- the full adaptive loop

    [Fact]
    public async Task A_blocked_worker_gets_a_diagnosis_the_plan_is_corrected_and_the_lesson_reaches_the_next_unit()
    {
        var executor = new FakeExecutor(
            // U1 attempt 1: the plan's assumption is wrong; the worker reports it instead of guessing.
            Do(Report("blocked", "missing_interface", "The plan says to use FeatureService, which does not exist.", "src/FeatureService.cs")),
            // U1 attempt 2: with the corrected instructions.
            Do(("app.txt", "feature v2\n"), Report("completed", description: "done")),
            // U2 attempt 1: repeats the corrected mistake; the lessons check catches it.
            Do(("DOCS.md", "Use FeatureService to enable it.\n")),
            // U2 attempt 2.
            Do(("DOCS.md", "Edit app.txt to enable it.\n")));
        var architect = new FakeArchitect(
            Decide(new
            {
                level = 1,
                root_cause = "plan_error",
                diagnosis = "My plan assumed a FeatureService; the feature is the content of app.txt.",
                instructions = "Write 'feature v2' into app.txt. There is no FeatureService.",
                plan_updates = new object[] { new { op = "update_unit", unit = "U2", constraints = new[] { "Describe app.txt, never FeatureService" } } },
                lessons = new object[] { new { text = "There is no FeatureService; the feature is app.txt.", scope = "global", forbidden_pattern = "FeatureService" } },
            }),
            Approve());

        var outcome = await Run(Plan(Feature(UnitRisk.High), Docs()), PolicyWithGate(ReviewConfig.Risk), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(["accepted", "accepted"], outcome.Units.Select(u => u.Status));
        Assert.Equal(1, outcome.Units[0].Interventions);
        Assert.Equal(UnitReviewVerdict.Approve, outcome.Units[0].Review);
        Assert.Equal(3, outcome.PlanRevision);

        // The architect saw the worker's report, and that its evidence does not exist.
        Assert.Equal(2, architect.Requests.Count);
        Assert.Equal(SupervisorPrompt.System, architect.Requests[0].System);
        Assert.Contains("missing_interface", architect.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("does not exist in the worktree", architect.Requests[0].User, StringComparison.Ordinal);
        Assert.Equal(UnitReviewPrompt.System, architect.Requests[1].System); // high-risk unit reviewed after its gates passed

        // The worker resumed with the corrected instructions, the lesson and the new revision.
        var resumed = executor.Calls[1].Brief.Body;
        Assert.Contains("Plan revision 3", resumed, StringComparison.Ordinal);
        Assert.Contains("Write 'feature v2' into app.txt", resumed, StringComparison.Ordinal);
        Assert.Contains("There is no FeatureService", resumed, StringComparison.Ordinal);
        Assert.Contains("The architect reviewed your attempt", resumed, StringComparison.Ordinal);

        // The next unit received the update to its own instructions and the global lesson.
        var u2 = executor.Calls[2].Brief.Body;
        Assert.Contains("Describe app.txt, never FeatureService", u2, StringComparison.Ordinal);
        Assert.Contains("(enforced by an automated check)", u2, StringComparison.Ordinal);
        Assert.Contains("A mistake the architect already corrected was made again", executor.Calls[3].Brief.Body, StringComparison.Ordinal);

        // The report file never reaches the branch; the fix and the docs do.
        Assert.DoesNotContain(WorkerReports.FileName, _repo.Git("ls-tree", "-r", "--name-only", outcome.Branch), StringComparison.Ordinal);
        Assert.Equal("feature v2\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));

        // Traceability: intervention → revision; original plan untouched; costs attributed by purpose.
        var summary = _ledger.GetSummary("run-1")!;
        var intervention = Assert.Single(summary.Interventions);
        Assert.Equal(("worker_blocked", 1, "resumed", "plan_error", 3), (intervention.Trigger, intervention.Level, intervention.Outcome, intervention.RootCause, intervention.PlanRevision));
        Assert.Equal(2, summary.Revisions.Count);
        Assert.Equal(1, summary.Stats.RepeatedMistakes);
        Assert.Equal([CallPurposes.Review, CallPurposes.Supervision], summary.Purposes.Where(p => p.Role == LlmRole.Architect).Select(p => p.Purpose).Order());
        var state = Artifacts.LoadPlanState()!;
        Assert.Equal(3, state.Revision);
        Assert.Empty(state.Original.Units[1].Constraints);
        Assert.Equal(UnitProgress.Accepted, state.Progress["U2"]);
    }

    // ---------------------------------------------------------------- intervention levels

    [Fact]
    public async Task L2_guided_correction_fixes_the_unit_and_commits_it_without_a_lock()
    {
        var policy = PolicyWithGate();
        policy.Escalation.MaxRetriesPerUnit = 1;
        var executor = new FakeExecutor(Do(("app.txt", "nothing\n")), Do(("DOCS.md", "docs\n")));
        var architect = new FakeArchitect(Decide(
            new { level = 2, root_cause = "worker_mistake", diagnosis = "The worker removed the feature line.", summary = "restored the feature line", why = "it deleted it" },
            FakeArchitect.Edit("app.txt", "nothing", "feature v2")));

        var outcome = await Run(Plan(Feature(), Docs()), policy, executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.StartsWith("architect correction", outcome.Units[0].Reason, StringComparison.Ordinal);
        Assert.Contains("tokonomy(U1): Feature [architect correction]", _repo.Git("log", "--format=%s", outcome.Branch), StringComparison.Ordinal);
        Assert.Equal("feature v2\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
        Assert.Contains("restored the feature line", executor.Calls[1].Brief.Body, StringComparison.Ordinal); // handoff to U2
        Assert.Equal(0, _ledger.GetStats("run-1").Takeovers);
        Assert.Equal(1, _ledger.GetStats("run-1").GuidedCorrections);
    }

    [Fact]
    public async Task L2_correction_that_does_not_pass_yet_lets_the_worker_continue_from_the_corrected_state()
    {
        var policy = PolicyWithGate();
        policy.Escalation.MaxRetriesPerUnit = 1;
        var executor = new FakeExecutor(
            Do(("app.txt", "nothing\n"), ("notes.txt", "keep me\n")),
            (brief, dir) =>
            {
                // The architect's partial fix is in place when the worker resumes.
                Assert.Equal("half way\n", File.ReadAllText(Path.Combine(dir, "app.txt")));
                File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
            });
        var architect = new FakeArchitect(Decide(
            new { level = 2, diagnosis = "Wrong text.", instructions = "Put the word feature back.", summary = "first step" },
            FakeArchitect.Edit("app.txt", "nothing", "half way")));

        var outcome = await Run(Plan(Feature()), policy, executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(2, outcome.Units[0].Attempts);
        var resumed = executor.Calls[1].Brief.Body;
        Assert.Contains("The architect applied a correction: first step", resumed, StringComparison.Ordinal);
        Assert.Contains("Put the word feature back.", resumed, StringComparison.Ordinal);
        Assert.Equal("keep me\n", _repo.Git("show", $"{outcome.Branch}:notes.txt"));
        Assert.Equal("resumed", _ledger.GetSummary("run-1")!.Interventions.Single().Outcome);
    }

    [Fact]
    public async Task L3_takeover_by_the_supervisor_is_locked_and_enforced_on_later_units()
    {
        var executor = new FakeExecutor(
            Do(("app.txt", "nothing\n")),
            Do(("app.txt", "nothing\n")),
            Do(("app.txt", "feature changed\n"), ("DOCS.md", "docs\n")),   // U2 breaks the lock
            Do(("app.txt", "feature by architect\n")));                    // and restores it
        var architect = new FakeArchitect(Decide(
            new { level = 3, root_cause = "worker_mistake", diagnosis = "The worker keeps deleting the feature.", summary = "rewrote app.txt", why = "it deleted the feature" },
            FakeArchitect.Edit("app.txt", "nothing", "feature by architect")));

        var outcome = await Run(Plan(Feature(), Docs()), PolicyWithGate(), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Contains("tokonomy(U1): Feature [architect takeover]", _repo.Git("log", "--format=%s", outcome.Branch), StringComparison.Ordinal);
        Assert.Contains("Locked regions were changed or removed in: app.txt", executor.Calls[3].Brief.Body, StringComparison.Ordinal);
        Assert.Equal(1, _ledger.GetStats("run-1").Takeovers);
        Assert.Equal(1, _ledger.GetStats("run-1").UnitsAcceptedWithoutTakeover);
    }

    [Fact]
    public async Task A_failed_takeover_resets_the_unit_and_the_worker_resumes_instead_of_failing_the_run()
    {
        var executor = new FakeExecutor(
            Do(("app.txt", "nothing\n")),
            Do(("app.txt", "nothing\n")),
            (_, dir) =>
            {
                Assert.Equal("feature v1\n", File.ReadAllText(Path.Combine(dir, "app.txt")).ReplaceLineEndings("\n")); // back at the unit start
                File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
            });
        var architect = new FakeArchitect(Decide(
            new { level = 3, diagnosis = "Rewriting it.", instructions = "Keep the word feature in app.txt." },
            FakeArchitect.Edit("app.txt", "nothing", "still nothing")));

        var outcome = await Run(Plan(Feature()), PolicyWithGate(), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(3, outcome.Units[0].Attempts);
        Assert.Contains("The architect's own fix did not pass", executor.Calls[2].Brief.Body, StringComparison.Ordinal);
        var intervention = _ledger.GetSummary("run-1")!.Interventions.Single();
        Assert.Equal((3, "resumed"), (intervention.Level, intervention.Outcome));
    }

    [Fact]
    public async Task A_repeated_identical_failure_calls_the_architect_before_the_retries_run_out()
    {
        var policy = PolicyWithGate();
        policy.Escalation.MaxRetriesPerUnit = 4;
        var executor = new FakeExecutor(Do(("app.txt", "nothing\n")), Do(("app.txt", "nothing\n")), Do(("app.txt", "feature v2\n")));
        var architect = new FakeArchitect(Decide(new { level = 1, diagnosis = "It deletes the word feature.", instructions = "Keep the word feature." }));

        var outcome = await Run(Plan(Feature()), policy, executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Single(architect.Requests);
        Assert.Contains("RepeatedFailure", architect.Requests[0].User, StringComparison.Ordinal);
        Assert.Equal(3, executor.Calls.Count);
    }

    [Fact]
    public async Task Touching_an_avoided_path_is_corrected_and_the_discarded_attempt_never_reaches_the_branch()
    {
        var unit = Feature() with { Avoid = ["README.md"] };
        var executor = new FakeExecutor(
            Do(("app.txt", "feature v2\n"), ("README.md", "# rewritten\n")),
            Do(("app.txt", "feature v2\n")));
        var architect = new FakeArchitect(Decide(new
        {
            level = 1,
            diagnosis = "The unit must not touch README.md.",
            instructions = "Change only app.txt.",
            discard_worker_changes = true,
        }));

        var outcome = await Run(Plan(unit), PolicyWithGate(), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Contains("ScopeDeviation", architect.Requests.Single().User, StringComparison.Ordinal);
        Assert.Equal("# demo\n", _repo.Git("show", $"{outcome.Branch}:README.md"));
        Assert.Contains("Your previous changes were discarded", executor.Calls[1].Brief.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unusable_supervision_reply_falls_back_to_the_classic_takeover()
    {
        var policy = PolicyWithGate();
        policy.Escalation.MaxRetriesPerUnit = 1;
        var executor = new FakeExecutor(Do(("app.txt", "nothing\n")));
        var architect = new FakeArchitect(
            Decide(new { level = 3, diagnosis = "I will rewrite it." }),          // L3 without edits: not usable
            FakeArchitect.Edit("app.txt", "nothing", "feature fixed"));

        var outcome = await Run(Plan(Feature()), policy, executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(TakeoverPrompt.System, architect.Requests[1].System);
        Assert.Equal("rejected", _ledger.GetSummary("run-1")!.Interventions.Single().Outcome);
    }

    [Fact]
    public async Task A_plan_update_that_targets_a_protected_path_is_rejected_but_the_lesson_and_instructions_still_apply()
    {
        var executor = new FakeExecutor(
            Do(Report("blocked", "plan_conflict", "the plan contradicts the code")),
            Do(("app.txt", "feature v2\n")));
        var architect = new FakeArchitect(Decide(new
        {
            level = 1,
            diagnosis = "Needs a policy change.",
            instructions = "Just edit app.txt.",
            plan_updates = new object[] { new { op = "update_unit", unit = "U1", modify = new[] { ".tokonomy/policy.yaml" } } },
            lessons = new object[] { new { text = "Never touch the policy.", scope = "global" } },
        }));

        var outcome = await Run(Plan(Feature()), PolicyWithGate(), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Contains("plan_update_rejected", File.ReadAllText(Artifacts.TranscriptPath), StringComparison.Ordinal);
        Assert.DoesNotContain(".tokonomy/policy.yaml", Artifacts.LoadPlanState()!.Current.Units[0].Modify);
        Assert.Contains("Never touch the policy.", executor.Calls[1].Brief.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_human_decision_stops_the_unit_and_leaves_the_branch_clean()
    {
        var executor = new FakeExecutor(Do(("app.txt", "feature v2\n"), Report("blocked", "missing_dependency", "needs the requests package")));
        var architect = new FakeArchitect(Decide(new { level = 0, diagnosis = "A new dependency is a product decision.", human_decision = "Approve adding 'requests'?" }));

        var outcome = await Run(Plan(Feature()), PolicyWithGate(), executor, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("needs a human decision: Approve adding 'requests'?", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(_repo.Head, outcome.HeadSha);
        Assert.Equal(1, _ledger.GetStats("run-1").HumanDecisions);
    }

    [Fact]
    public async Task With_interventions_switched_off_the_classic_behaviour_remains()
    {
        var policy = PolicyWithGate();
        policy.Supervision.MaxInterventionsPerUnit = 0;
        var executor = new FakeExecutor(Do(Report("blocked", "x", "y")), Do(("app.txt", "feature v2\n")));
        var architect = new FakeArchitect(Approve());

        var outcome = await Run(Plan(Feature()), policy, executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Empty(architect.Requests);
        Assert.Contains("worker made no changes", executor.Calls[1].Brief.Body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- decomposition

    [Fact]
    public async Task An_oversized_unit_is_split_into_smaller_units_that_inherit_its_acceptance()
    {
        var policy = PolicyWithGate();
        policy.Escalation.MaxDiffLines = 5;
        var big = string.Concat(Enumerable.Range(0, 12).Select(i => $"line {i}\n"));
        var executor = new FakeExecutor(
            Do(("app.txt", big)),                                   // oversized and the gate fails
            (brief, dir) =>
            {
                Assert.Equal("feature v1\n", File.ReadAllText(Path.Combine(dir, "app.txt")).ReplaceLineEndings("\n")); // reset to the unit start
                File.WriteAllText(Path.Combine(dir, "part1.txt"), "one\n");
            },
            Do(("app.txt", "feature v2\n")));
        var architect = new FakeArchitect(JsonSerializer.Serialize(new
        {
            reason = "two independent changes",
            parts = new object[]
            {
                new { id = "U1a", title = "Part one", spec = "write part1.txt", steps = new[] { "write part1.txt" }, files = new { create = new[] { "part1.txt" } } },
                new { id = "U1b", title = "Part two", spec = "update app.txt", steps = new[] { "write feature v2" }, files = new { modify = new[] { "app.txt" } } },
            },
        }));

        var outcome = await Run(Plan(Feature()), policy, executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal([("U1", "split"), ("U1a", "accepted"), ("U1b", "accepted")], outcome.Units.Select(u => (u.UnitId, u.Status)));
        Assert.Equal(DecomposePrompt.System, architect.Requests.Single().System);
        Assert.Contains("app.txt says feature v2", executor.Calls[2].Brief.Body, StringComparison.Ordinal); // the original acceptance moved to the last part
        var state = Artifacts.LoadPlanState()!;
        Assert.Equal(UnitProgress.Superseded, state.Progress["U1"]);
        Assert.Equal("split", _ledger.GetSummary("run-1")!.Interventions.Single().Outcome);
    }

    [Fact]
    public async Task Classic_strategy_4_still_fails_an_oversized_unit()
    {
        var policy = PolicyWithGate();
        policy.Escalation.MaxDiffLines = 5;
        var executor = new FakeExecutor(Do(("app.txt", string.Concat(Enumerable.Range(0, 12).Select(i => $"line {i}\n")))));
        var architect = new FakeArchitect("{}");

        var outcome = await Run(Plan(Feature()), policy, executor, architect, Strategy.Supervised);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("strategy 5 splits it", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(architect.Requests);
    }

    // ---------------------------------------------------------------- architectural review

    [Fact]
    public async Task A_review_that_requests_changes_sends_the_unit_back_with_its_findings()
    {
        var executor = new FakeExecutor(Do(("app.txt", "feature v2\n"), ("hack.txt", "global state\n")), Do(("app.txt", "feature v2\n")));
        var architect = new FakeArchitect(
            JsonSerializer.Serialize(new
            {
                verdict = "request_changes",
                summary = "unplanned file",
                findings = new object[] { new { severity = "high", category = "unrelated_change", file = "hack.txt", description = "hack.txt is not part of the plan", recommendation = "remove it" } },
            }),
            Approve());

        var outcome = await Run(Plan(Feature()), PolicyWithGate(ReviewConfig.Always), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Contains("hack.txt is not part of the plan", executor.Calls[1].Brief.Body, StringComparison.Ordinal);
        Assert.Equal(1, _ledger.GetStats("run-1").ReviewChangeRequests);
        Assert.Equal(1, _ledger.GetStats("run-1").ArchitecturalViolations);
    }

    [Fact]
    public async Task A_review_that_blocks_fails_the_unit_even_though_the_gates_passed()
    {
        var executor = new FakeExecutor(Do(("app.txt", "feature v2\n")));
        var architect = new FakeArchitect(JsonSerializer.Serialize(new { verdict = "blocked", summary = "violates ADR-1", findings = Array.Empty<object>() }));

        var outcome = await Run(Plan(Feature()), PolicyWithGate(ReviewConfig.Always), executor, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("the architectural review blocked the unit: violates ADR-1", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(_repo.Head, outcome.HeadSha);
    }

    [Theory]
    [InlineData(ReviewConfig.OnUnavailableAccept, RunStatus.Pass)]
    [InlineData(ReviewConfig.OnUnavailableFail, RunStatus.Fail)]
    public async Task An_unavailable_review_follows_policy(string onUnavailable, RunStatus expected)
    {
        var policy = PolicyWithGate(ReviewConfig.Always);
        policy.Review.OnUnavailable = onUnavailable;
        var executor = new FakeExecutor(Do(("app.txt", "feature v2\n")));

        var outcome = await Run(Plan(Feature()), policy, executor, new UnreachableArchitect());

        Assert.Equal(expected, outcome.Status);
        Assert.Equal(UnitReviewVerdict.Unavailable, _ledger.GetSummary("run-1")!.Reviews.Single().Verdict);
    }

    [Fact]
    public async Task A_review_never_runs_when_the_gates_failed()
    {
        var policy = PolicyWithGate(ReviewConfig.Always);
        policy.Escalation.MaxTakeoversPerUnit = 0;
        policy.Supervision.MaxInterventionsPerUnit = 0;
        var executor = new FakeExecutor(Do(("app.txt", "nothing\n")));
        var architect = new FakeArchitect(Approve());

        var outcome = await Run(Plan(Feature()), policy, executor, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Empty(architect.Requests);
    }

    /// <summary>An architect whose every call fails, like a provider outage.</summary>
    private sealed class UnreachableArchitect : IProvider
    {
        public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
