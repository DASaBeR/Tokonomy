using System.Globalization;
using System.Text;
using Tokonomy.Core.Model;

namespace Tokonomy.Bench;

public sealed record StrategyStats(
    string Strategy,
    int Trials,
    int Accepted,
    decimal TotalUsd,
    decimal? CostPerSuccess,
    decimal MinTrialUsd,
    decimal MaxTrialUsd,
    bool AnyEstimated)
{
    public double PassRate => Trials == 0 ? 0 : (double)Accepted / Trials;

    /// <summary>Fresh (uncached) prompt tokens across all trials.</summary>
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }
    public long CachedTokens { get; init; }
    /// <summary>Fresh input plus output; cache reads are listed separately.</summary>
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>All tokens spent ÷ accepted tasks: the token-denominated twin of <see cref="CostPerSuccess"/>.</summary>
    public long? TokensPerSuccess => Accepted == 0 ? null : TotalTokens / Accepted;

    /// <summary>Average wall-clock time per trial.</summary>
    public TimeSpan AverageDuration { get; init; }

    /// <summary>The same totals split by model, so the strong and the cheap model can be compared.</summary>
    public IReadOnlyList<ModelUsage> Models { get; init; } = [];

    /// <summary>Supervision totals across the strategy's trials (retries, takeovers, interventions, revisions...).</summary>
    public RunStats Supervision { get; init; } = new();

    /// <summary>Accepted units completed without an architect takeover, as a share of all accepted units.</summary>
    public double? UnitsWithoutTakeoverShare => Supervision.UnitsAccepted == 0 ? null : (double)Supervision.UnitsAcceptedWithoutTakeover / Supervision.UnitsAccepted;
}

/// <summary>Aggregates trials into the primary metric: total spend ÷ accepted tasks.</summary>
public static class BenchReport
{
    public static IReadOnlyList<StrategyStats> Summarize(IEnumerable<BenchResultRecord> results, IReadOnlyList<string>? strategyOrder = null)
    {
        var groups = results.GroupBy(r => r.Strategy, StringComparer.Ordinal).ToList();
        var order = (strategyOrder ?? groups.Select(g => g.Key).ToList()).ToList();
        return groups
            .OrderBy(g => order.IndexOf(g.Key) is var i && i >= 0 ? i : int.MaxValue)
            .Select(g =>
            {
                var trials = g.ToList();
                var accepted = trials.Count(t => t.Accepted);
                var total = trials.Sum(t => t.Usd);
                return new StrategyStats(
                    g.Key,
                    trials.Count,
                    accepted,
                    total,
                    accepted == 0 ? null : total / accepted,
                    trials.Min(t => t.Usd),
                    trials.Max(t => t.Usd),
                    trials.Any(t => t.CostSource == CostSource.Estimated))
                {
                    InputTokens = trials.Sum(t => t.InputTokens),
                    OutputTokens = trials.Sum(t => t.OutputTokens),
                    CachedTokens = trials.Sum(t => t.CachedTokens),
                    AverageDuration = TimeSpan.FromTicks((long)trials.Average(t => t.Duration.Ticks)),
                    Supervision = Sum(trials.Select(t => t.Stats)),
                    Models = trials.SelectMany(t => t.Models)
                        .GroupBy(m => m.Model, StringComparer.Ordinal)
                        .Select(m => new ModelUsage(m.Key, m.Sum(x => x.Calls), m.Sum(x => x.InputTokens), m.Sum(x => x.OutputTokens), m.Sum(x => x.CachedTokens), m.Sum(x => x.Usd)))
                        .OrderByDescending(m => m.InputTokens + m.OutputTokens)
                        .ToList(),
                };
            })
            .ToList();
    }

    public static string Markdown(string benchId, IReadOnlyList<BenchResultRecord> results, IReadOnlyList<string>? strategyOrder = null)
    {
        var stats = Summarize(results, strategyOrder);
        var tasks = results.Select(r => r.TaskId).Distinct(StringComparer.Ordinal).ToList();
        var repeats = results.Count == 0 ? 0 : results.Max(r => r.Repeat);
        var sb = new StringBuilder();
        sb.AppendLine($"# Benchmark {benchId}");
        sb.AppendLine();
        sb.AppendLine($"{tasks.Count} task(s) × {stats.Count} strateg{(stats.Count == 1 ? "y" : "ies")} × up to {repeats} repeat(s). " +
                      "Success = hidden acceptance check passed. Claims apply only to these tasks.");
        sb.AppendLine();
        sb.AppendLine("| Strategy | Trials | Passed | Pass rate | Total cost | Cost per success | Cost per trial (min–max) | Tokens (fresh in + out) | Tokens per success | Cache reads | Avg time per trial |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var s in stats)
        {
            var est = s.AnyEstimated ? " (est.)" : "";
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {s.Strategy} | {s.Trials} | {s.Accepted} | {s.PassRate * 100:0}% | ${s.TotalUsd:0.0000}{est} | " +
                $"{(s.CostPerSuccess is { } c ? $"${c:0.0000}{est}" : "n/a")} | ${s.MinTrialUsd:0.0000}–${s.MaxTrialUsd:0.0000} | " +
                $"{Compact(s.TotalTokens)} | {(s.TokensPerSuccess is { } tps ? Compact(tps) : "n/a")} | {Compact(s.CachedTokens)} | {Minutes(s.AverageDuration)} |"));
        }

        sb.AppendLine();
        sb.AppendLine("## Supervision");
        sb.AppendLine();
        sb.AppendLine("Totals over all trials of a strategy. Interventions are architect supervision calls (strategy 5); takeovers are architect-written units; " +
                      "repeated mistakes are failures of the lessons check (a corrected mistake made again); violations are high-severity review findings.");
        sb.AppendLine();
        sb.AppendLine("| Strategy | Units accepted | Without takeover | Worker retries | Takeovers | Interventions (L1 / L2) | Plan revisions | Review change requests | Violations | Repeated mistakes | Human decisions |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var s in stats)
        {
            var v = s.Supervision;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {s.Strategy} | {v.UnitsAccepted}/{v.Units} | {(s.UnitsWithoutTakeoverShare is { } share ? $"{share * 100:0}%" : "n/a")} | {v.WorkerRetries} | {v.Takeovers} | " +
                $"{v.Interventions} ({v.InstructionCorrections} / {v.GuidedCorrections}) | {v.PlanRevisions} | {v.ReviewChangeRequests} | {v.ArchitecturalViolations} | {v.RepeatedMistakes} | {v.HumanDecisions} |"));
        }

        sb.AppendLine();
        sb.AppendLine("## Quality and behaviour");
        sb.AppendLine();
        sb.AppendLine("Judge = an independent architect review of each trial's final diff against the task alone (same prompt for every strategy; " +
                      "not charged to trials). Unplanned = files changed that no unit of the final plan named (tests excluded).");
        sb.AppendLine();
        sb.AppendLine("| Strategy | Judge approved | Unplanned changes | Blocker reports | L3 takeovers | Failed takeovers | Splits | Lessons | Infra failures |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var s in stats)
        {
            var trials = results.Where(r => r.Strategy == s.Strategy).ToList();
            var judged = trials.Count(t => t.Stats.JudgeVerdict is "Approve" or "Concerns");
            var approved = trials.Count(t => t.Stats.JudgeVerdict == "Approve");
            var v = s.Supervision;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {s.Strategy} | {(judged == 0 ? "n/a" : $"{approved}/{judged}")} | {(v.UnplannedChanges?.ToString(CultureInfo.InvariantCulture) ?? "n/a")} | {v.BlockerReports} | " +
                $"{v.SupervisedTakeovers} | {v.FailedTakeovers} | {v.Splits} | {v.Lessons} | {trials.Count(IsInfrastructureFailure)} |"));
        }

        var purposes = stats.SelectMany(s => s.Supervision.UsdByPurpose.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (purposes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Cost by purpose (total over trials)");
            sb.AppendLine();
            sb.AppendLine("Shared plans are recorded once, in the trial that made them; the per-trial cost above charges them to every trial that used them.");
            sb.AppendLine();
            sb.AppendLine("| Strategy | " + string.Join(" | ", purposes) + " |");
            sb.AppendLine("|---|" + string.Concat(purposes.Select(_ => "---:|")));
            foreach (var s in stats)
            {
                sb.AppendLine($"| {s.Strategy} | " + string.Join(" | ", purposes.Select(p => string.Create(CultureInfo.InvariantCulture,
                    $"${s.Supervision.UsdByPurpose.GetValueOrDefault(p):0.0000}"))) + " |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Trials");
        sb.AppendLine();
        sb.AppendLine("| Task | Strategy | # | Accepted | Run | Cost | Time | Attempts | Retries | Interventions | Revisions | Judge | Unplanned | Note |");
        sb.AppendLine("|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|---:|---|");
        foreach (var r in results)
        {
            var note = (r.Note ?? "").Replace('|', '/').ReplaceLineEndings(" ");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.TaskId} | {r.Strategy} | {r.Repeat} | {(r.Accepted ? "yes" : "no")} | {r.RunStatus}{(IsInfrastructureFailure(r) ? " (infra)" : "")} | ${r.Usd:0.0000} | {Minutes(r.Duration)} | " +
                $"{r.Stats.WorkerAttempts} | {r.Stats.WorkerRetries} | {r.Stats.Interventions} | {r.Stats.PlanRevisions} | {r.Stats.JudgeVerdict ?? "-"} | " +
                $"{(r.Stats.UnplannedChanges?.ToString(CultureInfo.InvariantCulture) ?? "-")} | {(note.Length <= 160 ? note : note[..157] + "...")} |"));
        }

        sb.AppendLine();
        sb.AppendLine("## Tokens by model");
        sb.AppendLine();
        sb.AppendLine("Input is fresh (uncached) prompt tokens; cache reads are listed separately and billed at a lower rate. Antigravity counts its thinking tokens in the output.");
        sb.AppendLine();
        sb.AppendLine("| Strategy | Model | Calls | Input (fresh) | Cache reads | Output | Cost |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (var s in stats)
        {
            foreach (var m in s.Models)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {s.Strategy} | {m.Model} | {m.Calls} | {Compact(m.InputTokens)} | {Compact(m.CachedTokens)} | {Compact(m.OutputTokens)} | ${m.Usd:0.0000} |"));
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Per task (passed / trials)");
        sb.AppendLine();
        sb.AppendLine("| Task | " + string.Join(" | ", stats.Select(s => s.Strategy)) + " |");
        sb.AppendLine("|---|" + string.Concat(stats.Select(_ => "---:|")));
        foreach (var task in tasks)
        {
            var cells = stats.Select(s =>
            {
                var t = results.Where(r => r.TaskId == task && r.Strategy == s.Strategy).ToList();
                return $"{t.Count(r => r.Accepted)}/{t.Count}";
            });
            sb.AppendLine($"| {task} | {string.Join(" | ", cells)} |");
        }

        var estimated = stats.Any(s => s.AnyEstimated);
        if (estimated)
        {
            sb.AppendLine();
            sb.AppendLine("Costs marked (est.) include calls that were not metered or models without a configured price.");
        }

        return sb.ToString();
    }

    internal static RunStats Sum(IEnumerable<RunStats> all)
    {
        var list = all.ToList();
        return new RunStats
        {
            Units = list.Sum(x => x.Units),
            UnitsAccepted = list.Sum(x => x.UnitsAccepted),
            UnitsAcceptedWithoutTakeover = list.Sum(x => x.UnitsAcceptedWithoutTakeover),
            WorkerAttempts = list.Sum(x => x.WorkerAttempts),
            WorkerRetries = list.Sum(x => x.WorkerRetries),
            Takeovers = list.Sum(x => x.Takeovers),
            Interventions = list.Sum(x => x.Interventions),
            InstructionCorrections = list.Sum(x => x.InstructionCorrections),
            GuidedCorrections = list.Sum(x => x.GuidedCorrections),
            PlanRevisions = list.Sum(x => x.PlanRevisions),
            ReviewChangeRequests = list.Sum(x => x.ReviewChangeRequests),
            ArchitecturalViolations = list.Sum(x => x.ArchitecturalViolations),
            RepeatedMistakes = list.Sum(x => x.RepeatedMistakes),
            HumanDecisions = list.Sum(x => x.HumanDecisions),
            BlockerReports = list.Sum(x => x.BlockerReports),
            FailedTakeovers = list.Sum(x => x.FailedTakeovers),
            Splits = list.Sum(x => x.Splits),
            Lessons = list.Sum(x => x.Lessons),
            SupervisedTakeovers = list.Sum(x => x.SupervisedTakeovers),
            UnplannedChanges = list.Any(x => x.UnplannedChanges is not null) ? list.Sum(x => x.UnplannedChanges ?? 0) : null,
            UsdByPurpose = list.SelectMany(x => x.UsdByPurpose).GroupBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value), StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// A trial that ended because of the environment (provider outage, usage limit, sandbox), not because of the
    /// strategy. It still counts as a failed trial in the totals; the report lists it so it can be re-run and discussed.
    /// </summary>
    public static bool IsInfrastructureFailure(BenchResultRecord r) =>
        r.RunStatus == RunStatus.Error && r.Note is { } n &&
        InfrastructureSignals.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] InfrastructureSignals =
        ["architect call failed", "rate limit", "usage limit", "429", "503", "overloaded", "gate isolation unavailable", "sandbox", "timed out waiting", "connection"];

    /// <summary>1234 → "1.2k", 3_400_000 → "3.40M": enough precision to compare strategies at a glance.</summary>
    internal static string Compact(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (n / 1000d).ToString("0", CultureInfo.InvariantCulture) + "k",
        >= 1_000 => (n / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    internal static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
}
