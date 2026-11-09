using System.Text.Json;
using Tokonomy.Core.Model;

namespace Tokonomy.Runtime.Executors;

/// <summary>
/// Reads usage from Claude Code's <c>--output-format json</c> result. Uses the per-model <c>modelUsage</c> block
/// when present (input includes cache-creation tokens, cached is cache-read), otherwise the top-level <c>usage</c>.
/// Returns an empty list when the output is not a recognisable result, so a format change degrades to "estimated".
/// </summary>
public static class ClaudeUsageParser
{
    public static IReadOnlyList<WorkerUsage> Parse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        foreach (var candidate in Candidates(stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var usage = FromRoot(doc.RootElement);
                if (usage.Count > 0)
                {
                    return usage;
                }
            }
            catch (JsonException)
            {
            }
        }

        return [];
    }

    // The result is one JSON object; also try the last non-empty line to tolerate leading noise.
    private static IEnumerable<string> Candidates(string stdout)
    {
        yield return stdout.Trim();
        var last = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (last is not null)
        {
            yield return last;
        }
    }

    private static List<WorkerUsage> FromRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (root.TryGetProperty("modelUsage", out var models) && models.ValueKind == JsonValueKind.Object)
        {
            var list = new List<WorkerUsage>();
            foreach (var m in models.EnumerateObject())
            {
                var v = m.Value;
                list.Add(new WorkerUsage(
                    m.Name,
                    Long(v, "inputTokens") + Long(v, "cacheCreationInputTokens"),
                    Long(v, "outputTokens"),
                    Long(v, "cacheReadInputTokens"),
                    Decimal(v, "costUSD")));
            }

            if (list.Count > 0)
            {
                return list;
            }
        }

        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            return
            [
                new WorkerUsage(
                    "unknown",
                    Long(u, "input_tokens") + Long(u, "cache_creation_input_tokens"),
                    Long(u, "output_tokens"),
                    Long(u, "cache_read_input_tokens"),
                    Decimal(root, "total_cost_usd")),
            ];
        }

        return [];
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;

    private static decimal? Decimal(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : null;
}
