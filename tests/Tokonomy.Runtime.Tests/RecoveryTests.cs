using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Supervision;
using Tokonomy.Core.Takeover;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Runtime.Tests;

public class RunRecoveryTests
{
    private static WorkUnit U(string id, params string[] deps) => new() { Id = id, Title = id, Spec = "do " + id, DependsOn = deps };

    private static readonly Contract Plan = new() { Task = "t", Units = [U("U1"), U("U2", "U1"), U("U3", "U2")] };

    private static PlanState State(Action<PlanManager>? edit = null)
    {
        var plan = new PlanManager(Plan);
        edit?.Invoke(plan);
        return plan.Snapshot();
    }

    private static (string, string) Commit(string subject) => (Guid.NewGuid().ToString("N"), subject);

    private static RecoveryDecision Reconcile(ResumeRequest request, (string, string)[]? commits = null,
        AttemptRecord[]? attempts = null, InterventionRecord[]? interventions = null, PlanRevisionRecord[]? revisions = null) =>
        RunRecovery.Reconcile("run-1", Plan, request, commits ?? [], attempts ?? [], interventions ?? [], revisions ?? []);

    private static RunState Checkpoint(string phase = RunPhases.Idle, params LockedRegion[] locks) =>
        new() { RunId = "run-1", Phase = phase, Locks = locks };

    [Fact]
    public void A_run_interrupted_before_any_state_was_saved_starts_from_the_original_plan()
    {
        var decision = Reconcile(new ResumeRequest());

        Assert.True(decision.Safe);
        Assert.Equal(1, decision.Plan!.Revision);
        Assert.All(decision.Plan.Progress.Values, p => Assert.Equal(UnitProgress.Pending, p));
    }

    [Fact]
    public void Completed_units_without_a_saved_plan_state_are_refused()
    {
        var decision = Reconcile(new ResumeRequest(), [Commit("tokonomy(U1): U1")]);

        Assert.False(decision.Safe);
        Assert.Contains("plan-state.json is missing", decision.Problems.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupt_plan_state_is_refused_not_guessed()
    {
        var decision = Reconcile(new ResumeRequest { PlanStateError = "'{' is invalid after a value" });

        Assert.False(decision.Safe);
        Assert.Contains("unreadable", decision.Problems.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_state_from_another_plan_is_refused()
    {
        var other = new PlanManager(Plan with { Task = "something else" }).Snapshot();

        Assert.False(Reconcile(new ResumeRequest { PlanState = other, RunState = Checkpoint() }).Safe);
    }

    [Fact]
    public void The_branch_not_the_saved_flag_decides_what_is_complete()
    {
        // U1: committed and accepted. U2: committed but the crash came before its state was saved. U3: saved as
        // accepted (a damaged state) but there is no commit, so it is not complete.
        var state = State(p =>
        {
            p.MarkAccepted("U1");
            p.MarkRunning("U2");
        }) is var s ? s with { Progress = new Dictionary<string, UnitProgress> { ["U1"] = UnitProgress.Accepted, ["U2"] = UnitProgress.Running, ["U3"] = UnitProgress.Pending } } : null!;

        var decision = Reconcile(new ResumeRequest { PlanState = state, RunState = Checkpoint(RunPhases.Unit) },
            [Commit("tokonomy(U1): acceptance tests"), Commit("tokonomy(U1): U1"), Commit("tokonomy(U2): U2 [architect correction]")]);

        Assert.True(decision.Safe, string.Join("; ", decision.Problems));
        Assert.Equal(UnitProgress.Accepted, decision.Plan!.Progress["U1"]);
        Assert.Equal(UnitProgress.Accepted, decision.Plan.Progress["U2"]);
        Assert.Contains(decision.Notes, n => n.Contains("U2 was committed", StringComparison.Ordinal));
        Assert.Contains("U1", decision.TestsCommitted);
    }

    [Fact]
    public void A_unit_saved_as_accepted_without_its_commit_runs_again()
    {
        var state = State(p => p.MarkAccepted("U1"));

        var decision = Reconcile(new ResumeRequest { PlanState = state, RunState = Checkpoint() });

        Assert.True(decision.Safe);
        Assert.Equal(UnitProgress.Pending, decision.Plan!.Progress["U1"]);
    }

    [Theory]
    [InlineData("hand-made commit")]
    [InlineData("tokonomy(U1): U1")]
    [InlineData("tokonomy(U9): U9")]
    [InlineData("tokonomy(U2): U2")]
    public void Ambiguous_or_tampered_branches_are_refused(string extra)
    {
        var state = State(p => p.MarkAccepted("U1"));

        var decision = Reconcile(new ResumeRequest { PlanState = state, RunState = Checkpoint() },
            extra == "tokonomy(U2): U2" ? [Commit(extra)] : [Commit("tokonomy(U1): U1"), Commit(extra)]);

        Assert.False(decision.Safe);
    }

    [Fact]
    public void A_failed_unit_is_a_verdict_not_an_interruption()
    {
        var state = State(p => p.MarkFailed("U1"));

        var decision = Reconcile(new ResumeRequest { PlanState = state, RunState = Checkpoint() });

        Assert.False(decision.Safe);
        Assert.Contains("--again", decision.Problems.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Revisions_saved_but_missing_from_the_ledger_are_recorded_once()
    {
        var state = State(p =>
        {
            p.Apply(new PlanUpdate { BaseRevision = 1, Source = "supervision", Reason = "a", Operations = [new PlanOperation { Op = PlanOps.AddDont, Text = "x" }] });
            p.Apply(new PlanUpdate { BaseRevision = 2, Source = "supervision", Reason = "b", Operations = [new PlanOperation { Op = PlanOps.AddDont, Text = "y" }] });
        });

        var decision = Reconcile(new ResumeRequest { PlanState = state, RunState = Checkpoint() },
            revisions: [new PlanRevisionRecord("run-1", 2, DateTimeOffset.UtcNow, "supervision", "a", null, 1, false)]);

        Assert.Equal([3], decision.RevisionsToRecord.Select(r => r.Number));
    }

    [Fact]
    public void Ledger_revisions_that_never_reached_the_plan_state_are_kept_for_audit_only()
    {
        var decision = Reconcile(new ResumeRequest { PlanState = State(), RunState = Checkpoint() },
            revisions: [new PlanRevisionRecord("run-1", 2, DateTimeOffset.UtcNow, "supervision", "lost", null, 1, false)]);

        Assert.True(decision.Safe);
        Assert.Equal(1, decision.Plan!.Revision);
        Assert.Contains(decision.Notes, n => n.Contains("never reached plan-state.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_checkpoint_a_run_that_had_a_takeover_is_refused_because_its_locks_are_unknown()
    {
        var state = State(p => p.MarkAccepted("U1"));
        AttemptRecord[] takeover = [new() { RunId = "run-1", UnitId = "U1", Number = 3, Executor = "architect", Decision = DecisionKind.Accept, Reason = "takeover: gates pass" }];

        Assert.False(Reconcile(new ResumeRequest { PlanState = state }, [Commit("tokonomy(U1): U1 [architect takeover]")], takeover).Safe);
        Assert.False(Reconcile(new ResumeRequest { PlanState = state, RunStateError = "truncated" }, [Commit("tokonomy(U1): U1 [architect takeover]")], takeover).Safe);
        Assert.True(Reconcile(new ResumeRequest { PlanState = state, RunState = Checkpoint(RunPhases.Idle, new LockedRegion("a.py", "x")) },
            [Commit("tokonomy(U1): U1 [architect takeover]")], takeover).Safe);
    }

    [Fact]
    public void A_checkpoint_of_another_run_is_refused() =>
        Assert.False(Reconcile(new ResumeRequest { PlanState = State(), RunState = new RunState { RunId = "other", Phase = RunPhases.Idle } }).Safe);

    [Fact]
    public void Per_unit_history_keeps_caps_across_the_restart()
    {
        var decision = Reconcile(new ResumeRequest { PlanState = State(p => p.MarkRunning("U1")), RunState = Checkpoint(RunPhases.Supervision) },
            attempts:
            [
                new() { RunId = "run-1", UnitId = "U1", Number = 1, Executor = "fake", Decision = DecisionKind.Retry, Reason = "x" },
                new() { RunId = "run-1", UnitId = "U1", Number = 2, Executor = "fake", Decision = DecisionKind.Takeover, Reason = "x" },
                new() { RunId = "run-1", UnitId = "U1", Number = 3, Executor = "architect", Decision = DecisionKind.Fail, Reason = "takeover: gate(s) still failing" },
            ],
            interventions: [new() { RunId = "run-1", UnitId = "U1", At = DateTimeOffset.UtcNow, Trigger = "retry_limit", Level = 1, Outcome = "resumed" }]);

        var history = decision.History["U1"];
        Assert.Equal(new UnitHistory(2, 1, 1, true), history);
        Assert.Equal(1, decision.Run!.RunInterventions);
        Assert.Contains(decision.Notes, n => n.Contains("interrupted during supervision", StringComparison.Ordinal));
    }
}

public class RunPersistenceTests
{
    [Fact]
    public void Only_one_process_can_hold_a_run()
    {
        using var tmp = new TempDir();
        var artifacts = new RunArtifacts(tmp.Path, "run-1");

        using (artifacts.AcquireLock())
        {
            Assert.Throws<RunInProgressException>(() => artifacts.AcquireLock());
        }

        using var again = artifacts.AcquireLock(); // released with its holder
    }

    [Fact]
    public void The_checkpoint_round_trips_with_locks_and_handoffs()
    {
        using var tmp = new TempDir();
        var artifacts = new RunArtifacts(tmp.Path, "run-1");
        var lockRegion = new LockedRegion("app.py", "def f():\n    return 1");

        artifacts.SaveRunState(new RunState
        {
            RunId = "run-1",
            Phase = RunPhases.Supervision,
            CurrentUnit = "U2",
            UnitStart = "abc",
            Locks = [lockRegion],
            Handoffs = [new Handoff { UnitId = "U1", UnitTitle = "a", Files = ["app.py"], Summary = "s", Why = "w", Locked = [lockRegion] }],
            RunInterventions = 2,
        });
        var loaded = artifacts.LoadRunState()!;

        Assert.Equal(RunPhases.Supervision, loaded.Phase);
        Assert.Equal(lockRegion, loaded.Locks.Single());
        Assert.Equal("U1", loaded.Handoffs.Single().UnitId);
        Assert.False(File.Exists(artifacts.RunStatePath + ".tmp"));
    }

    [Fact]
    public void A_truncated_state_file_is_reported_as_unreadable()
    {
        using var tmp = new TempDir();
        var artifacts = new RunArtifacts(tmp.Path, "run-1");
        Directory.CreateDirectory(artifacts.Directory);
        File.WriteAllText(artifacts.PlanStatePath, "{\"original\": {\"task\": \"t\", \"units\": [");

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => artifacts.LoadPlanState());
    }

    [Fact]
    public void An_interrupted_write_never_replaces_the_last_good_state()
    {
        using var tmp = new TempDir();
        var artifacts = new RunArtifacts(tmp.Path, "run-1");
        artifacts.SaveRunState(new RunState { RunId = "run-1", Phase = RunPhases.Idle });
        File.WriteAllText(artifacts.RunStatePath + ".tmp", "{ half"); // a crash between writing the temp file and the rename

        Assert.Equal(RunPhases.Idle, artifacts.LoadRunState()!.Phase);
    }
}

public class GuardedProviderTests
{
    private sealed class Failing(Exception ex) : IProvider
    {
        public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct) => throw ex;
    }

    [Fact]
    public async Task A_provider_timeout_becomes_architect_unavailable()
    {
        var guarded = GuardedProvider.Wrap(new Failing(new TaskCanceledException("HttpClient.Timeout of 1800 seconds elapsing")))!;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => guarded.CompleteAsync(new ProviderRequest("m", "s", "u"), default));
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuardedProvider.Wrap(new Failing(new TimeoutException()))!.CompleteAsync(new ProviderRequest("m", "s", "u"), default));
    }

    [Fact]
    public async Task A_cancellation_the_user_asked_for_is_not_hidden()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GuardedProvider.Wrap(new Failing(new OperationCanceledException(cts.Token)))!.CompleteAsync(new ProviderRequest("m", "s", "u"), cts.Token));
    }
}

public class ArchitectChangeReviewPolicyTests
{
    private static (bool, string) Should(ReviewConfig c, ReviewSignals s) => ReviewPolicy.ShouldReviewArchitectChange(c, s);

    [Fact]
    public void Small_in_plan_architect_fixes_are_not_reviewed_again() =>
        Assert.False(Should(new ReviewConfig(), new ReviewSignals { Risk = UnitRisk.Medium, DiffLines = 40, Intervened = true }).Item1);

    [Fact]
    public void Risky_large_or_out_of_plan_architect_changes_get_an_independent_review()
    {
        var config = new ReviewConfig();

        Assert.True(Should(config, new ReviewSignals { Risk = UnitRisk.High }).Item1);
        Assert.True(Should(config, new ReviewSignals { DiffLines = 2 * config.MediumRiskDiffLines }).Item1);
        Assert.True(Should(config, new ReviewSignals { Deviation = new PlanDeviation([], ["other.py"], []) }).Item1);
    }

    [Fact]
    public void It_can_be_switched_off_or_forced()
    {
        Assert.False(Should(new ReviewConfig { ArchitectChanges = ReviewConfig.Off }, new ReviewSignals { Risk = UnitRisk.High }).Item1);
        Assert.False(Should(new ReviewConfig { Mode = ReviewConfig.Off }, new ReviewSignals { Risk = UnitRisk.High }).Item1);
        Assert.True(Should(new ReviewConfig { ArchitectChanges = ReviewConfig.Always }, new ReviewSignals()).Item1);
    }
}
