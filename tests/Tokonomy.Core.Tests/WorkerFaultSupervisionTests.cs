using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;

namespace Tokonomy.Core.Tests;

public class WorkerFaultSupervisionTests
{
    private static readonly Policy Policy = new();   // max_retries_per_unit = 2

    private static UnitState State(int attempts, params string[] changed) => new()
    {
        Unit = new WorkUnit { Id = "U1", Title = "t", Spec = "s" },
        Attempts = attempts,
        ChangedPaths = changed,
        DiffLines = changed.Length * 3,
    };

    private static RunResult Run(WorkerFault? fault, bool timedOut = false) =>
        new() { ExitCode = 0, Duration = TimeSpan.FromSeconds(1), TimedOut = timedOut, Fault = fault };

    private static GateResult Passed() => new() { Gate = "test", Status = GateStatus.Passed };

    [Theory]
    [InlineData(WorkerFaultKind.ProcessFailed)]
    [InlineData(WorkerFaultKind.ReportedError)]
    [InlineData(WorkerFaultKind.PermissionDenied)]
    [InlineData(WorkerFaultKind.NoResult)]
    public void A_faulted_run_is_never_accepted_even_with_a_clean_diff_and_passing_gates(WorkerFaultKind kind)
    {
        var decision = new Supervisor().Decide(State(1, "a.py"), Run(new WorkerFault(kind, "d")), [Passed()], Policy);

        Assert.Equal(DecisionKind.Retry, decision.Kind);
        Assert.Contains(kind.ToString(), decision.Reason, StringComparison.Ordinal);
        Assert.Contains("d", decision.Reason, StringComparison.Ordinal);   // the retry feedback explains what went wrong
    }

    [Fact]
    public void Repeated_faults_escalate_to_an_explicit_takeover()
    {
        var decision = new Supervisor().Decide(State(2, "a.py"), Run(new WorkerFault(WorkerFaultKind.PermissionDenied, "x")), [Passed()], Policy);

        Assert.Equal(DecisionKind.Takeover, decision.Kind);
        Assert.Equal(TakeoverTrigger.WorkerFault, decision.Trigger);
        Assert.True(decision.CanTakeOver);
    }

    [Fact]
    public void A_fault_with_no_changes_is_reported_as_the_fault_not_as_no_changes()
    {
        var decision = new Supervisor().Decide(State(1), Run(new WorkerFault(WorkerFaultKind.PermissionDenied, "x")), [], Policy);

        Assert.Equal(DecisionKind.Retry, decision.Kind);
        Assert.Contains("PermissionDenied", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_mismatch_fails_immediately_and_cannot_be_taken_over()
    {
        var decision = new Supervisor().Decide(State(1, "a.py"), Run(new WorkerFault(WorkerFaultKind.ModelMismatch, "wrong")), [Passed()], Policy);

        Assert.Equal(DecisionKind.Fail, decision.Kind);
        Assert.False(decision.CanTakeOver);
        Assert.Contains("wrong model", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_mismatch_beats_every_other_rule_including_a_protected_path()
    {
        var policy = new Policy { Escalation = { ProtectedPaths = ["src/**"] } };
        var decision = new Supervisor().Decide(State(1, "src/x.py"), Run(new WorkerFault(WorkerFaultKind.ModelMismatch, "wrong")), [], policy);

        Assert.Equal(DecisionKind.Fail, decision.Kind);
    }

    [Fact]
    public void A_clean_run_is_still_accepted()
    {
        var decision = new Supervisor().Decide(State(1, "a.py"), Run(null), [Passed()], Policy);
        Assert.Equal(DecisionKind.Accept, decision.Kind);
    }

    [Fact]
    public void Timeout_handling_is_unchanged()
    {
        var decision = new Supervisor().Decide(State(1, "a.py"), Run(null, timedOut: true), [], Policy);
        Assert.Equal(TakeoverTrigger.Timeout, decision.Trigger);
    }
}
