#pragma warning disable CA1861 // inline arrays describe scripted architect replies
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Supervision;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>
/// Crash recovery with real git, gates and ledger. A "crash" is an exception from the scripted worker or architect that
/// no fallback handles (the run ends in Error, as after Ctrl+C); the process-level test (<see cref="ProcessCrashTests"/>)
/// covers a hard kill.
/// </summary>
public sealed class ResumeOrchestratorTests : IDisposable
{
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"), ("README.md", "# demo\n"));
    private readonly string _config;
    private readonly SqliteLedger _ledger;

    public ResumeOrchestratorTests()
    {
        _config = Path.Combine(_repo.Path, ".tokonomy");
        _ledger = new SqliteLedger(Path.Combine(_config, "ledger.db"));
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = "5", TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
    }

    public void Dispose()
    {
        _ledger.Dispose();
        _repo.Dispose();
    }

    private RunArtifacts Artifacts => new(_config, "run-1");

    private sealed class Crash(string message) : IOException(message);

    private static Action<Brief, string> Do(params (string File, string Content)[] writes) => (_, dir) =>
    {
        foreach (var (file, content) in writes)
        {
            File.WriteAllText(Path.Combine(dir, file), content);
        }
    };

    private static Action<Brief, string> CrashNow(params (string File, string Content)[] writesFirst) => (brief, dir) =>
    {
        Do(writesFirst)(brief, dir);
        throw new Crash("the machine went down");
    };

    private static Policy PolicyWithGate(bool keepWorktree = false) => new()
    {
        Models = { Architect = "strong", Worker = "cheap" },
        GateExecution = new GateExecutionConfig { Provider = GateExecutionConfig.HostUnsafeProvider },
        Gates = [new GateConfig { Name = "has-feature", Cmd = Shell.FileContains("app.txt", "feature") }],
        Review = { Mode = ReviewConfig.Off },
        Isolation = { KeepWorktree = keepWorktree },
    };

    private static Contract Plan() => new()
    {
        Task = "Ship feature v2 with docs",
        SchemaVersion = 2,
        Units =
        [
            new WorkUnit { Id = "U1", Title = "Feature", Spec = "app.txt says feature v2", Modify = ["app.txt"], Files = ["app.txt"], Steps = ["edit app.txt"] },
            new WorkUnit { Id = "U2", Title = "Docs", Spec = "write DOCS.md", Create = ["DOCS.md"], Files = ["DOCS.md"], Steps = ["write DOCS.md"], DependsOn = ["U1"] },
        ],
    };

    private ResumeRequest LoadResume()
    {
        var a = Artifacts;
        return new ResumeRequest { PlanState = a.LoadPlanState(), RunState = a.LoadRunState() };
    }

    private async Task<RunOutcome> Run(Policy policy, FakeExecutor executor, IProvider? architect, Strategy strategy, bool resume = false, Contract? plan = null) =>
        await new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor, architect: architect).RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = plan ?? Plan(),
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = Artifacts,
            WorkerModel = "cheap",
            UseGates = strategy.UseGates,
            AllowEscalation = strategy.AllowEscalation,
            AdaptiveSupervision = strategy.AdaptiveSupervision,
            MaxAttempts = strategy.MaxAttempts,
            AlreadySpentUsd = _ledger.GetRun("run-1")!.TotalUsd,
            Resume = resume ? LoadResume() : null,
        }, null, default);

    private string Subjects(string branch) => _repo.Git("log", "--format=%s", branch);

    private int CountRows(string sql)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(_config, "ledger.db")};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Decide(object decision, string edits = "") => "```json\n" + JsonSerializer.Serialize(decision) + "\n```\n" + edits;

    [Fact]
    public async Task Strategy_4_resumes_after_a_completed_unit_without_running_it_again()
    {
        var first = new FakeExecutor(Do(("app.txt", "feature v2\n")), CrashNow());
        var crashed = await Run(PolicyWithGate(), first, null, Strategy.Supervised);
        Assert.Equal(RunStatus.Error, crashed.Status);

        var second = new FakeExecutor(Do(("DOCS.md", "docs\n")));
        var outcome = await Run(PolicyWithGate(), second, null, Strategy.Supervised, resume: true);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(["U2"], outcome.Units.Select(u => u.UnitId));               // U1 is not executed again
        Assert.All(second.Calls, c => Assert.StartsWith("U2-a", c.Brief.Label, StringComparison.Ordinal));
        Assert.Contains("U1: Feature", second.Calls[0].Brief.Body, StringComparison.Ordinal); // and the worker builds on it
        Assert.Equal(1, Subjects(outcome.Branch).Split('\n').Count(s => s.StartsWith("tokonomy(U1): Feature", StringComparison.Ordinal)));
        Assert.Equal("U2-a1", second.Calls[0].Brief.Label);                       // the crashed attempt was never recorded
        Assert.Equal(RunStatus.Pass, _ledger.GetRun("run-1")!.Status);
        Assert.Contains("\"resumed\"", File.ReadAllText(Artifacts.TranscriptPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_interrupted_unit_restarts_from_its_last_commit_and_its_unverified_work_is_saved_not_reused()
    {
        var first = new FakeExecutor(CrashNow(("app.txt", "half-done work\n")));
        await Run(PolicyWithGate(keepWorktree: true), first, null, Strategy.Supervised); // the worktree stays, like after a hard kill

        var second = new FakeExecutor(
            (_, dir) =>
            {
                Assert.Equal("feature v1\n", File.ReadAllText(Path.Combine(dir, "app.txt")).ReplaceLineEndings("\n"));
                File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
            },
            Do(("DOCS.md", "docs\n")));
        var outcome = await Run(PolicyWithGate(), second, null, Strategy.Supervised, resume: true);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        var patch = Assert.Single(Directory.GetFiles(Path.Combine(Artifacts.Directory, "recovery"), "*.patch"));
        Assert.Contains("half-done work", File.ReadAllText(patch), StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_an_instruction_correction_the_resumed_worker_gets_the_latest_revision_and_nothing_is_duplicated()
    {
        var first = new FakeExecutor(
            Do((Tokonomy.Core.Supervision.WorkerReports.FileName, """{"status":"blocked","reason":"missing_interface","description":"no FeatureService"}""")),
            CrashNow());
        var architect = new FakeArchitect(Decide(new
        {
            level = 1,
            root_cause = "plan_error",
            diagnosis = "There is no FeatureService.",
            instructions = "Edit app.txt directly.",
            plan_updates = new object[] { new { op = "update_unit", unit = "U2", constraints = new[] { "mention app.txt" } } },
            lessons = new object[] { new { text = "No FeatureService exists.", scope = "global" } },
        }));
        Assert.Equal(RunStatus.Error, (await Run(PolicyWithGate(), first, architect, Strategy.Adaptive)).Status);
        var callsBefore = CountRows("SELECT COUNT(*) FROM llm_calls");
        var revisionsBefore = CountRows("SELECT COUNT(*) FROM plan_revisions");

        var second = new FakeExecutor(Do(("app.txt", "feature v2\n")), Do(("DOCS.md", "about app.txt\n")));
        var outcome = await Run(PolicyWithGate(), second, architect, Strategy.Adaptive, resume: true);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Single(architect.Requests);                                         // the intervention is not repeated
        var brief = second.Calls[0].Brief.Body;
        Assert.Equal("U1-a2", second.Calls[0].Brief.Label);                       // numbering continues after the recorded attempt
        Assert.Contains("Plan revision 3", brief, StringComparison.Ordinal);
        Assert.Contains("Edit app.txt directly.", brief, StringComparison.Ordinal);
        Assert.Contains("No FeatureService exists.", brief, StringComparison.Ordinal);
        Assert.Contains("mention app.txt", second.Calls[1].Brief.Body, StringComparison.Ordinal);
        Assert.Equal(1, CountRows("SELECT COUNT(*) FROM interventions"));
        Assert.Equal(revisionsBefore, CountRows("SELECT COUNT(*) FROM plan_revisions"));
        Assert.Equal(callsBefore, CountRows("SELECT COUNT(*) FROM llm_calls"));   // nothing billed twice
    }

    [Fact]
    public async Task An_intervention_interrupted_mid_call_is_asked_again_once_and_recorded_once()
    {
        var first = new FakeExecutor(Do((Tokonomy.Core.Supervision.WorkerReports.FileName, """{"status":"blocked","reason":"x","description":"stuck"}""")));
        var crashing = new CrashingArchitect();
        Assert.Equal(RunStatus.Error, (await Run(PolicyWithGate(), first, crashing, Strategy.Adaptive)).Status);
        Assert.Equal(RunPhases.Supervision, Artifacts.LoadRunState()!.Phase);
        Assert.Equal(0, CountRows("SELECT COUNT(*) FROM interventions"));

        var architect = new FakeArchitect(Decide(new { level = 1, diagnosis = "Just edit app.txt.", instructions = "Write feature v2 into app.txt." }));
        var second = new FakeExecutor(
            Do((Tokonomy.Core.Supervision.WorkerReports.FileName, """{"status":"blocked","reason":"x","description":"stuck"}""")),
            Do(("app.txt", "feature v2\n")),
            Do(("DOCS.md", "d\n")));
        var outcome = await Run(PolicyWithGate(), second, architect, Strategy.Adaptive, resume: true);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Single(architect.Requests);
        Assert.Equal(1, CountRows("SELECT COUNT(*) FROM interventions"));
    }

    [Fact]
    public async Task Locks_from_a_takeover_before_the_crash_are_still_enforced_after_resume()
    {
        var policy = PolicyWithGate();
        policy.Escalation.MaxRetriesPerUnit = 1;
        var first = new FakeExecutor(Do(("app.txt", "nothing\n")), CrashNow());
        var architect = new FakeArchitect(Decide(new { level = 3, diagnosis = "rewrite", summary = "fixed", why = "deleted" },
            FakeArchitect.Edit("app.txt", "nothing", "feature by architect")));
        Assert.Equal(RunStatus.Error, (await Run(policy, first, architect, Strategy.Adaptive)).Status);

        var second = new FakeExecutor(
            Do(("app.txt", "feature changed\n"), ("DOCS.md", "d\n")),   // breaks the architect's locked line
            Do(("app.txt", "feature by architect\n")));
        var outcome = await Run(PolicyWithGate(), second, architect, Strategy.Adaptive, resume: true);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Single(architect.Requests);                                         // the takeover is not applied again
        Assert.Contains("Locked regions were changed or removed in: app.txt", second.Calls[1].Brief.Body, StringComparison.Ordinal);
        Assert.Equal(1, Subjects(outcome.Branch).Split('\n').Count(s => s.Contains("[architect takeover]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_guided_correction_before_the_crash_is_not_repeated_and_the_unfinished_unit_is_reviewed_again()
    {
        var policy = PolicyWithGate();
        policy.Escalation.MaxRetriesPerUnit = 1;
        policy.Review.Mode = ReviewConfig.Always;
        policy.Review.ArchitectChanges = ReviewConfig.Off;
        var first = new FakeExecutor(Do(("app.txt", "nothing\n")), Do(("DOCS.md", "d\n")), CrashNow());
        var architect = new FakeArchitect(
            Decide(new { level = 2, diagnosis = "deleted the word", summary = "restored it" }, FakeArchitect.Edit("app.txt", "nothing", "feature v2")),
            JsonSerializer.Serialize(new { verdict = "request_changes", summary = "explain more", findings = new[] { new { severity = "medium", description = "too short" } } }));
        Assert.Equal(RunStatus.Error, (await Run(policy, first, architect, Strategy.Adaptive)).Status);
        Assert.Equal(2, architect.Requests.Count);

        var resumedArchitect = new FakeArchitect(JsonSerializer.Serialize(new { verdict = "approve", summary = "ok", findings = Array.Empty<object>() }));
        var second = new FakeExecutor(Do(("DOCS.md", "docs explaining the feature\n")));
        var outcome = await Run(policy, second, resumedArchitect, Strategy.Adaptive, resume: true);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(["U2"], outcome.Units.Select(u => u.UnitId));
        Assert.Single(resumedArchitect.Requests);                                 // only U2's review; U1's correction stands
        Assert.Equal(1, CountRows("SELECT COUNT(*) FROM interventions WHERE level = 2"));
        Assert.Equal(0, CountRows("SELECT COUNT(*) FROM unit_reviews WHERE unit_id = 'U1'"));
        Assert.Equal(2, CountRows("SELECT COUNT(*) FROM unit_reviews WHERE unit_id = 'U2'")); // the pre-crash review is kept, U2 is reviewed again
    }

    [Fact]
    public async Task A_plan_revision_that_reached_the_state_but_not_the_ledger_is_recorded_on_resume()
    {
        var first = new FakeExecutor(
            Do((Tokonomy.Core.Supervision.WorkerReports.FileName, """{"status":"blocked","reason":"x","description":"y"}""")),
            CrashNow());
        var architect = new FakeArchitect(Decide(new { level = 1, diagnosis = "d", instructions = "Edit app.txt.", lessons = new object[] { new { text = "L", scope = "global" } } }));
        await Run(PolicyWithGate(), first, architect, Strategy.Adaptive);
        var saved = Artifacts.LoadPlanState()!.Revisions.Count;
        using (var c = new SqliteConnection($"Data Source={Path.Combine(_config, "ledger.db")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM plan_revisions WHERE revision = (SELECT MAX(revision) FROM plan_revisions)"; // the crash window
            cmd.ExecuteNonQuery();
        }

        var outcome = await Run(PolicyWithGate(), new FakeExecutor(Do(("app.txt", "feature v2\n")), Do(("DOCS.md", "d\n"))), architect, Strategy.Adaptive, resume: true);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(saved, CountRows("SELECT COUNT(DISTINCT revision) FROM plan_revisions"));
        Assert.Equal(saved, CountRows("SELECT COUNT(*) FROM plan_revisions"));
    }

    [Fact]
    public async Task A_branch_changed_by_hand_is_refused()
    {
        var first = new FakeExecutor(Do(("app.txt", "feature v2\n")), CrashNow());
        var crashed = await Run(PolicyWithGate(), first, null, Strategy.Supervised);
        _repo.Git("checkout", "-q", crashed.Branch);
        _repo.Write("README.md", "# edited by hand\n");
        _repo.Git("-c", "user.name=T", "-c", "user.email=t@e", "commit", "-qam", "manual edit");
        _repo.Git("checkout", "-q", "main");

        var outcome = await Run(PolicyWithGate(), new FakeExecutor(Do(("DOCS.md", "d\n"))), null, Strategy.Supervised, resume: true);

        Assert.Equal(RunStatus.Error, outcome.Status);
        Assert.Contains("was not made by Tokonomy", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_corrupt_plan_state_is_refused()
    {
        await Run(PolicyWithGate(), new FakeExecutor(Do(("app.txt", "feature v2\n")), CrashNow()), null, Strategy.Supervised);
        File.WriteAllText(Artifacts.PlanStatePath, "{ \"original\": ");

        var request = LoadResumeTolerant();
        var outcome = await new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => new FakeExecutor(Do(("DOCS.md", "d\n")))).RunAsync(new RunRequest
        {
            RunId = "run-1", Contract = Plan(), RepoRoot = _repo.Path, BaseSha = _repo.Head, Policy = PolicyWithGate(), ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"] }, Artifacts = Artifacts, Resume = request,
        }, null, default);

        Assert.Equal(RunStatus.Error, outcome.Status);
        Assert.Contains("plan-state.json is unreadable", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepted_units_that_no_longer_pass_their_gates_stop_the_resume()
    {
        await Run(PolicyWithGate(), new FakeExecutor(Do(("app.txt", "feature v2\n")), CrashNow()), null, Strategy.Supervised);
        var stricter = PolicyWithGate();
        stricter.Gates = [new GateConfig { Name = "needs-v3", Cmd = Shell.WhenChanged("grep -q v3 app.txt || exit 1", "if ((Get-Content -Raw app.txt) -notmatch 'v3') { exit 1 }") }];

        var outcome = await Run(stricter, new FakeExecutor(Do(("DOCS.md", "d\n"))), null, Strategy.Supervised, resume: true);

        Assert.Equal(RunStatus.Error, outcome.Status);
        Assert.Contains("no longer pass their gates", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_held_by_another_process_cannot_be_entered()
    {
        using var held = Artifacts.AcquireLock();

        var outcome = await Run(PolicyWithGate(), new FakeExecutor(Do(("app.txt", "feature v2\n"))), null, Strategy.Supervised);

        Assert.Equal(RunStatus.Error, outcome.Status);
        Assert.Contains("in use by another Tokonomy process", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(RunStatus.Planned, _ledger.GetRun("run-1")!.Status); // untouched
    }

    private ResumeRequest LoadResumeTolerant()
    {
        PlanState? plan = null;
        string? error = null;
        try
        {
            plan = Artifacts.LoadPlanState();
        }
        catch (JsonException ex)
        {
            error = ex.Message;
        }

        return new ResumeRequest { PlanState = plan, PlanStateError = error, RunState = Artifacts.LoadRunState() };
    }

    /// <summary>An architect whose call dies in a way no fallback covers, like the process being killed mid-call.</summary>
    private sealed class CrashingArchitect : IProvider
    {
        public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct) => throw new Crash("power cut during the architect call");
    }
}
