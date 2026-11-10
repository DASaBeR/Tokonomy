using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tokonomy.Runtime.Config;

namespace Tokonomy.Runtime.Providers;

/// <summary>Result of checking an API key: whether the service accepted it, and a sentence to show.</summary>
public sealed record KeyCheck(bool Ok, string Message);

/// <summary>
/// Model menus and key checks for any OpenAI-compatible service, so a provider other than OpenRouter works the same way.
/// A key is only ever sent to the service it belongs to, in an Authorization header.
/// </summary>
public static class ApiModels
{
    private static readonly string[] NotChatModels =
        ["embed", "whisper", "tts", "dall-e", "moderation", "image", "audio", "realtime", "transcribe", "rerank", "vision-preview", "imagen", "veo"];

    /// <summary>Checks the key with the lightest authenticated call the service has. A network failure is reported as such, not as a bad key.</summary>
    public static async Task<KeyCheck> CheckKeyAsync(HttpClient http, string baseUrl, string key, bool openRouter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var url = openRouter ? "https://openrouter.ai/api/v1/auth/key" : baseUrl.TrimEnd('/') + "/models";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new KeyCheck(false, "the service rejected this key"),
                _ when response.IsSuccessStatusCode => new KeyCheck(true, "the key works"),
                _ => new KeyCheck(true, $"could not confirm the key (HTTP {(int)response.StatusCode}); continuing"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new KeyCheck(true, "could not reach the service to check the key (offline?); continuing");
        }
    }

    /// <summary>
    /// The chat models a service offers (<c>GET {base}/models</c>), newest first when it says when they were made, filtered of
    /// embedding, speech and image models. Empty when the service does not list models or the call fails.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListAsync(HttpClient http, string baseUrl, string? key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? ParseList(await response.Content.ReadAsStringAsync(ct)) : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> ParseList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray()
            .Select(m => (Id: m.TryGetProperty("id", out var id) ? id.GetString() : null,
                          Created: m.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0))
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => (Id: m.Id!.StartsWith("models/", StringComparison.Ordinal) ? m.Id[7..] : m.Id, m.Created))
            .Where(m => !NotChatModels.Any(bad => m.Id.Contains(bad, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => m.Created)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => m.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The models to offer for <paramref name="service"/>: Anthropic's own list is fixed (its OpenAI-compatible layer does not list), the others are asked.</summary>
    public static async Task<IReadOnlyList<string>> ForServiceAsync(HttpClient http, ApiService service, string? key, CancellationToken ct) =>
        service.Id == Sources.Anthropic.Id
            ? Sources.ClaudeModels.Select(m => m.Id).ToList()
            : await ListAsync(http, service.BaseUrl, key, ct);
}
