using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Taskrail.Llm.Providers;

/// <summary>USD per million tokens.</summary>
public sealed class ModelPrice
{
    public decimal Input { get; set; }
    public decimal Output { get; set; }

    /// <summary>Price for cache-hit input tokens; defaults to <see cref="Input"/> when unset.</summary>
    public decimal? CachedInput { get; set; }

    /// <summary>Price for tokens written to the prompt cache (Anthropic-style); defaults to <see cref="Input"/>.</summary>
    public decimal? CacheWrite { get; set; }

    /// <summary>
    /// Long-context threshold. When a request's total input exceeds this many tokens, the whole request is priced
    /// with <see cref="Above"/> instead. Must be set together with <see cref="Above"/>.
    /// </summary>
    public long? AboveInputTokens { get; set; }

    public ModelPriceTier? Above { get; set; }
}

/// <summary>Rates that replace the base rates above the long-context threshold. Unset cache rates default to <see cref="Input"/>.</summary>
public sealed class ModelPriceTier
{
    public decimal Input { get; set; }
    public decimal Output { get; set; }
    public decimal? CachedInput { get; set; }
    public decimal? CacheWrite { get; set; }
}

/// <summary>User-maintained prices from <c>.taskrail/prices.yaml</c>. No prices are built in; they go stale.</summary>
public sealed class PriceTable
{
    private const decimal PerMillion = 1_000_000m;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private readonly Dictionary<string, ModelPrice> _prices;

    public PriceTable(IDictionary<string, ModelPrice> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        foreach (var (model, price) in prices)
        {
            Validate(model, price);
        }

        _prices = new Dictionary<string, ModelPrice>(prices, StringComparer.OrdinalIgnoreCase);
    }

    public static PriceTable Empty { get; } = new(new Dictionary<string, ModelPrice>());

    public IReadOnlyCollection<string> Models => _prices.Keys;

    public static PriceTable Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        var file = Deserializer.Deserialize<PricesFile>(File.ReadAllText(path)) ?? new PricesFile();
        return new PriceTable(file.Models);
    }

    public bool Has(string model) => _prices.ContainsKey(model);

    /// <summary>
    /// Returns the cost in USD, or null when the model has no configured price.
    /// <paramref name="inputTokens"/> is the total prompt size and includes cached and cache-write tokens.
    /// </summary>
    public decimal? Cost(string model, long inputTokens, long outputTokens, long cachedTokens, long cacheWriteTokens = 0)
    {
        if (!_prices.TryGetValue(model, out var p))
        {
            return null;
        }

        var (input, cachedRate, writeRate, output) = p.AboveInputTokens is { } threshold && p.Above is { } above && inputTokens > threshold
            ? (above.Input, above.CachedInput ?? above.Input, above.CacheWrite ?? above.Input, above.Output)
            : (p.Input, p.CachedInput ?? p.Input, p.CacheWrite ?? p.Input, p.Output);

        var cached = Math.Clamp(cachedTokens, 0, inputTokens);
        var written = Math.Clamp(cacheWriteTokens, 0, inputTokens - cached);
        var uncached = inputTokens - cached - written;
        return ((uncached * input) + (cached * cachedRate) + (written * writeRate) + (outputTokens * output)) / PerMillion;
    }

    private static void Validate(string model, ModelPrice price)
    {
        if ((price.AboveInputTokens is null) != (price.Above is null))
        {
            throw new InvalidDataException($"prices: model '{model}' must set above_input_tokens and above together.");
        }

        if (price.AboveInputTokens is <= 0)
        {
            throw new InvalidDataException($"prices: model '{model}' above_input_tokens must be positive.");
        }

        decimal?[] rates =
        [
            price.Input, price.Output, price.CachedInput, price.CacheWrite,
            price.Above?.Input, price.Above?.Output, price.Above?.CachedInput, price.Above?.CacheWrite,
        ];
        if (rates.Any(r => r < 0))
        {
            throw new InvalidDataException($"prices: model '{model}' has a negative price.");
        }
    }

    private sealed class PricesFile
    {
        public Dictionary<string, ModelPrice> Models { get; set; } = [];
    }
}
