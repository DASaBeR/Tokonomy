using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tokonomy.Llm.Proxy;

/// <summary>
/// Extracts token usage from OpenAI-style and Anthropic-style responses, whole-body or streamed (SSE).
/// Streaming usage arrives in pieces (Anthropic: message_start + message_delta; OpenAI: final chunk), so each
/// field keeps its maximum.
/// </summary>
public sealed class UsageAccumulator
{
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CachedTokens { get; private set; }

    /// <summary>Tokens written to the prompt cache (billed at a different rate by some providers). Part of <see cref="InputTokens"/>.</summary>
    public long CacheWriteTokens { get; private set; }

    public string? Model { get; private set; }
    public bool Seen { get; private set; }

    public void AddJson(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        Model ??= obj["model"]?.GetValueKind() == JsonValueKind.String ? obj["model"]!.GetValue<string>() : null;
        Read(obj["usage"] as JsonObject);

        // Anthropic stream: {"type":"message_start","message":{"model":..,"usage":{..}}}
        if (obj["message"] is JsonObject message)
        {
            Model ??= message["model"]?.GetValueKind() == JsonValueKind.String ? message["model"]!.GetValue<string>() : null;
            Read(message["usage"] as JsonObject);
        }
    }

    /// <summary>Feeds one complete SSE line.</summary>
    public void AddSseLine(string line)
    {
        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            var payload = line[5..].Trim();
            if (payload.Length > 0 && payload != "[DONE]")
            {
                AddJson(payload);
            }
        }
    }

    private void Read(JsonObject? usage)
    {
        if (usage is null)
        {
            return;
        }

        Seen = true;
        if (Num(usage, "prompt_tokens") is { } prompt)
        {
            // OpenAI: prompt_tokens includes cached tokens.
            InputTokens = Math.Max(InputTokens, prompt);
            OutputTokens = Math.Max(OutputTokens, Num(usage, "completion_tokens") ?? 0);
            var details = usage["prompt_tokens_details"] as JsonObject;
            CachedTokens = Math.Max(CachedTokens, Num(details, "cached_tokens") ?? 0);

            // Gateways that front Anthropic models report cache writes under varying names; take whichever is present.
            var written = Num(details, "cache_write_tokens") ?? Num(details, "cache_creation_tokens") ?? Num(usage, "cache_creation_input_tokens") ?? 0;
            CacheWriteTokens = Math.Max(CacheWriteTokens, written);
            return;
        }

        // Anthropic: input_tokens excludes cache reads and cache writes.
        var input = Num(usage, "input_tokens");
        var cacheRead = Num(usage, "cache_read_input_tokens") ?? 0;
        var cacheWrite = Num(usage, "cache_creation_input_tokens") ?? 0;
        if (input is not null)
        {
            InputTokens = Math.Max(InputTokens, input.Value + cacheRead + cacheWrite);
            CachedTokens = Math.Max(CachedTokens, cacheRead);
            CacheWriteTokens = Math.Max(CacheWriteTokens, cacheWrite);
        }

        OutputTokens = Math.Max(OutputTokens, Num(usage, "output_tokens") ?? 0);
    }

    private static long? Num(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
}
