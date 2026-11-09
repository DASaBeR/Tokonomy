using System.Text.Json;
using Tokonomy.Core.Model;

namespace Tokonomy.Runtime.Executors;

/// <summary>
/// Reads usage from the Antigravity CLI (<c>agy -p ... --output-format json</c>). The result carries token counts
/// but no cost and no model name, so the model comes from the run and cost stays unknown (recorded as estimated).
/// Thinking tokens are counted as output. Returns an empty list when the output is not a recognisable result.
/// </summary>
public static class AgyUsageParser
{
    public static IReadOnlyList<WorkerUsage> Parse(string stdout, string? model)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        foreach (var candidate in new[] { stdout.Trim(), stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "" })
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    return
                    [
                        new WorkerUsage(
                            string.IsNullOrWhiteSpace(model) ? "agy" : model,
                            Long(u, "input_tokens"),
                            Long(u, "output_tokens") + Long(u, "thinking_tokens"),
                            Long(u, "cache_read_tokens"),
                            null),
                    ];
                }
            }
            catch (JsonException)
            {
            }
        }

        return [];
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
}
