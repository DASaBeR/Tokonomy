using Tokonomy.Bench;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Orchestration;

namespace Tokonomy.E2E.Tests;

public class BenchSupervisionReportTests
{
    private static BenchResultRecord Trial(string strategy, bool ok, RunStats stats) => new()
    {
        BenchId = "b1", TaskId = "t", Strategy = strategy, Repeat = 1, RunId = "r-" + Guid.NewGuid().ToString("N"), RunStatus = RunStatus.Pass,
        Accepted = ok, Usd = 0.1m, Duration = TimeSpan.FromMinutes(2), Stats = stats,
    };

    [Fact]
    public void Supervision_metrics_are_totalled_per_strategy()
    {
        var results = new[]
        {
            Trial("4", true, new RunStats { Units = 3, UnitsAccepted = 3, UnitsAcceptedWithoutTakeover = 2, WorkerRetries = 4, Takeovers = 1 }),
            Trial("5", true, new RunStats { Units = 3, UnitsAccepted = 3, UnitsAcceptedWithoutTakeover = 3, WorkerRetries = 1, Interventions = 2, InstructionCorrections = 1, GuidedCorrections = 1, PlanRevisions = 2, RepeatedMistakes = 0 }),
            Trial("5", false, new RunStats { Units = 2, UnitsAccepted = 1, UnitsAcceptedWithoutTakeover = 1, Interventions = 1, HumanDecisions = 1, ArchitecturalViolations = 2 }),
        };

        var stats = BenchReport.Summarize(results, ["4", "5"]).ToDictionary(s => s.Strategy);
        var markdown = BenchReport.Markdown("b1", results, ["4", "5"]);

        Assert.Equal(3, stats["5"].Supervision.Interventions);
        Assert.Equal(4, stats["5"].Supervision.UnitsAccepted);
        Assert.Equal(1.0, stats["5"].UnitsWithoutTakeoverShare);
        Assert.Equal(2.0 / 3, stats["4"].UnitsWithoutTakeoverShare!.Value, 3);
        Assert.Contains("## Supervision", markdown, StringComparison.Ordinal);
        Assert.Contains("| 5 | 4/5 | 100% | 1 | 0 | 3 (1 / 1) | 2 | 0 | 2 | 0 | 1 |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Quality_cost_by_purpose_and_every_trial_are_reported_and_infrastructure_failures_are_marked()
    {
        var results = new[]
        {
            Trial("4", true, new RunStats { UnplannedChanges = 1, JudgeVerdict = "Concerns", UsdByPurpose = new Dictionary<string, decimal> { ["architect:planning"] = 0.3m } }),
            Trial("5", true, new RunStats { UnplannedChanges = 0, JudgeVerdict = "Approve", BlockerReports = 1, Lessons = 2, UsdByPurpose = new Dictionary<string, decimal> { ["architect:supervision"] = 0.1m } }),
            Trial("5", false, new RunStats()) with { RunStatus = RunStatus.Error, Note = "the architect call failed: 529 overloaded" },
        };

        var markdown = BenchReport.Markdown("b1", results, ["4", "5"]);

        Assert.Contains("| 4 | 0/1 | 1 | 0 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 5 | 1/1 | 0 | 1 | 0 | 0 | 0 | 2 | 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("architect:supervision", markdown, StringComparison.Ordinal);
        Assert.Contains("Error (infra)", markdown, StringComparison.Ordinal);
        Assert.True(BenchReport.IsInfrastructureFailure(results[2]));
        Assert.False(BenchReport.IsInfrastructureFailure(results[0]));
    }

    [Fact]
    public void Strategy_5_is_adaptive_and_opt_in_while_strategy_4_stays_the_default()
    {
        var adaptive = Strategy.Parse("5");

        Assert.True(adaptive.AdaptiveSupervision);
        Assert.True(adaptive.UsesArchitectPlan && adaptive.UseGates && adaptive.AllowEscalation);
        Assert.False(Strategy.Supervised.AdaptiveSupervision);
        Assert.Same(Strategy.Supervised, Strategy.Default);
    }
}
