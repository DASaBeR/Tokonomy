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
