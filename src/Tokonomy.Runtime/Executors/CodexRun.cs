using System.Text.Json;
using Tokonomy.Core.Model;

namespace Tokonomy.Runtime.Executors;

/// <summary>
/// What a <c>codex exec --json</c> run reported: the final message, the token usage and any error. Codex prints one JSON
/// event per line (<c>item.completed</c> with an <c>agent_message</c>, <c>turn.completed</c> with <c>usage</c>,
/// <c>turn.failed</c> / <c>error</c>). Unknown or malformed lines are ignored, so a format change degrades to "no usage".
/// </summary>
public sealed record CodexRun(string Message, long InputTokens, long CachedTokens, long OutputTokens, bool HasUsage, string? Error, int Commands)
{
    public static CodexRun Parse(string stdout)
    {
        var message = "";
        string? error = null;
        long input = 0, cached = 0, output = 0;
        var usage = false;
        var commands = 0;

        foreach (var line in (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line[0] != '{')
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                switch (type)
                {
                    case "item.completed" when root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object:
                        var itemType = item.TryGetProperty("type", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() : null;
                        if (itemType == "agent_message" && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            message = text.GetString() ?? "";
                        }
                        else if (itemType == "command_execution")
                        {
                            commands++;
                        }

                        break;
                    case "turn.completed" when root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object:
                        usage = true;
                        input += Number(u, "input_tokens");
                        cached += Number(u, "cached_input_tokens");
                        output += Number(u, "output_tokens");
                        break;
                    case "turn.failed" or "error":
                        error = ErrorText(root) ?? error ?? "the run failed";
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }

        return new CodexRun(message, input, cached, output, usage, error, commands);
    }

    /// <summary>One usage record for the ledger; empty when Codex reported none (the cost is then simply estimated).</summary>
    public IReadOnlyList<WorkerUsage> ToUsage(string? model) =>
        HasUsage ? [new WorkerUsage(string.IsNullOrWhiteSpace(model) ? "codex" : model, InputTokens, OutputTokens, CachedTokens, null)] : [];

    private static long Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string? ErrorText(JsonElement root)
    {
        if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
        {
            return m.GetString();
        }

        return root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
               && e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String
            ? em.GetString()
            : null;
    }
}
