using System.Globalization;
using System.Text.Json;

namespace Tokonomy.Runtime.Providers;

/// <summary>One OpenRouter model, with prices in USD per million tokens.</summary>
public sealed record CatalogModel(string Id, string Name, decimal InputPerM, decimal OutputPerM, int ContextLength, long Created)
{
    public bool IsFree => InputPerM == 0 && OutputPerM == 0;

    /// <summary>Vendor prefix of the id (<c>anthropic</c> in <c>anthropic/claude-sonnet-5-5</c>).</summary>
    public string Vendor => Id.Contains('/') ? Id[..Id.IndexOf('/', StringComparison.Ordinal)] : Id;
}

/// <summary>
/// OpenRouter lists hundreds of models. This fetches the public list (no key needed) and keeps the ones worth showing in
/// a menu: models that can call tools (a coding agent needs that), from the vendors people actually use, newest first,
/// plus a few free ones.
/// </summary>
public static class OpenRouterCatalog
{
    public const string ModelsUrl = "https://openrouter.ai/api/v1/models";
    public const int DefaultCount = 20;
    public const int MinFree = 2;
    private const int MinContext = 32_000;
    private const int PerVendor = 3;

    /// <summary>Vendors in the order they are offered. Anything else only fills spare room.</summary>
    internal static readonly string[] VendorPriority =
    [
        "anthropic", "openai", "google", "deepseek", "qwen", "x-ai", "moonshotai", "z-ai", "mistralai", "meta-llama", "minimax", "nvidia",
    ];

    public static async Task<IReadOnlyList<CatalogModel>> FetchAsync(HttpClient http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        using var response = await http.GetAsync(new Uri(ModelsUrl), ct);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Text-in/text-out models that support tools. Prices are converted from per-token to per-million.</summary>
    public static IReadOnlyList<CatalogModel> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var models = new List<CatalogModel>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var m in data.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || !SupportsTools(m) || !OutputsText(m))
            {
                continue;
            }

            var context = m.TryGetProperty("context_length", out var ctx) && ctx.ValueKind == JsonValueKind.Number ? ctx.GetInt32() : 0;
            if (context < MinContext || !TryPrices(m, out var input, out var output))
            {
                continue;
            }

            models.Add(new CatalogModel(
                id,
                m.TryGetProperty("name", out var n) ? n.GetString() ?? id : id,
                input,
                output,
                context,
                m.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0));
        }

        return models;
    }

    /// <summary>
    /// About <paramref name="count"/> models: up to three newest paid ones per vendor, vendors in priority order, and at least
    /// <paramref name="minFree"/> free ones (when that many exist).
    /// </summary>
    public static IReadOnlyList<CatalogModel> Curate(IEnumerable<CatalogModel> all, int count = DefaultCount, int minFree = MinFree)
    {
        ArgumentNullException.ThrowIfNull(all);
        var models = all.ToList();
        var free = models.Where(m => m.IsFree).OrderByDescending(m => m.ContextLength >= 128_000).ThenByDescending(m => m.Created).ToList();
        var freeWanted = Math.Min(free.Count, Math.Max(minFree, Math.Min(4, count / 5)));
        var paidWanted = count - freeWanted;

        var paid = models.Where(m => !m.IsFree)
            .GroupBy(m => m.Vendor, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Created).Take(PerVendor).ToList(), StringComparer.OrdinalIgnoreCase);

        var picked = new List<CatalogModel>();
        var others = paid.Keys.Except(VendorPriority, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();

        // Round-robin over the main vendors first (newest of each, then the second newest, ...), so one vendor cannot crowd
        // out the rest; minor vendors only fill whatever room is left.
        foreach (var vendors in new[] { VendorPriority.ToList(), others })
        {
            for (var round = 0; round < PerVendor && picked.Count < paidWanted; round++)
            {
                foreach (var vendor in vendors)
                {
                    if (picked.Count < paidWanted && paid.TryGetValue(vendor, out var list) && round < list.Count)
                    {
                        picked.Add(list[round]);
                    }
                }
            }
        }

        picked.AddRange(free.Take(freeWanted));
        return picked;
    }

    /// <summary>For picking a strong model: priciest (usually most capable) first. Free models go last.</summary>
    public static IReadOnlyList<CatalogModel> ForStrongRole(IEnumerable<CatalogModel> curated) =>
        curated.OrderBy(m => m.IsFree).ThenByDescending(m => m.OutputPerM).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();

    /// <summary>For picking a cheap model: free first, then cheapest.</summary>
    public static IReadOnlyList<CatalogModel> ForCheapRole(IEnumerable<CatalogModel> curated) =>
        curated.OrderByDescending(m => m.IsFree).ThenBy(m => m.OutputPerM).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();

    private static bool SupportsTools(JsonElement m) =>
        m.TryGetProperty("supported_parameters", out var p) && p.ValueKind == JsonValueKind.Array &&
        p.EnumerateArray().Any(x => x.GetString() == "tools");

    private static bool OutputsText(JsonElement m)
    {
        if (!m.TryGetProperty("architecture", out var a) || !a.TryGetProperty("output_modalities", out var o) || o.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var outputs = o.EnumerateArray().Select(x => x.GetString()).ToList();
        return outputs.Contains("text") && outputs.Count == 1;
    }

    private static bool TryPrices(JsonElement m, out decimal input, out decimal output)
    {
        input = output = 0;
        if (!m.TryGetProperty("pricing", out var p))
        {
            return false;
        }

        return TryPerMillion(p, "prompt", out input) && TryPerMillion(p, "completion", out output);
    }

    private static bool TryPerMillion(JsonElement pricing, string name, out decimal perMillion)
    {
        perMillion = 0;
        if (!pricing.TryGetProperty(name, out var el))
        {
            return false;
        }

        var text = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var perToken) || perToken < 0)
        {
            // OpenRouter uses "-1" for variable-priced routers; those cannot be budgeted.
            return false;
        }

        perMillion = Math.Round(perToken * 1_000_000m, 4);
        return true;
    }
}
