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
                    trials.Any(t => t.CostSource == CostSource.Estimated));
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
        sb.AppendLine("| Strategy | Trials | Passed | Pass rate | Total cost | Cost per success | Cost per trial (min–max) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var s in stats)
        {
            var est = s.AnyEstimated ? " (est.)" : "";
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {s.Strategy} | {s.Trials} | {s.Accepted} | {s.PassRate * 100:0}% | ${s.TotalUsd:0.0000}{est} | " +
                $"{(s.CostPerSuccess is { } c ? $"${c:0.0000}{est}" : "n/a")} | ${s.MinTrialUsd:0.0000}–${s.MaxTrialUsd:0.0000} |"));
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
}
