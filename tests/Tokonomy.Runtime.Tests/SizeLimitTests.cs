using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Orchestration;

namespace Tokonomy.Runtime.Tests;

public sealed class SizeLimitTests
{
    private static RunRequest Request(bool useGates, bool allowEscalation, string dir) => new()
    {
        RunId = "r",
        Contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "t", Spec = "s" }] },
        RepoRoot = dir,
        BaseSha = "x",
        Policy = new Policy(),
        ExecutorName = "f",
        Executor = new ExecutorDefinition { Cmd = ["f"] },
        Artifacts = new RunArtifacts(dir, "r"),
        WorkerModel = "m",
        UseGates = useGates,
        AllowEscalation = allowEscalation,
    };

    private static readonly Decision Split = new(DecisionKind.Split, "diff has 491 changed lines (limit 400)");

    [Fact]
    public void A_strategy_without_gates_keeps_its_work_whatever_the_diff_size()
    {
        using var dir = new TempDir();
        var request = Request(useGates: false, allowEscalation: false, dir.Path);

        var decision = RunOrchestrator.Constrain(Split, request, new UnitState { Unit = request.Contract.Units[0], Attempts = 1 }, 1, architectAvailable: false);

        Assert.Equal(DecisionKind.Accept, decision.Kind);
        Assert.Contains("without gates", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_strategy_with_gates_but_no_architect_still_fails_an_unverified_oversized_diff()
    {
        using var dir = new TempDir();
        var request = Request(useGates: true, allowEscalation: false, dir.Path);

        var decision = RunOrchestrator.Constrain(Split, request, new UnitState { Unit = request.Contract.Units[0], Attempts = 1 }, 1, architectAvailable: false);

        Assert.Equal(DecisionKind.Fail, decision.Kind);
    }
}
