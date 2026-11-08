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
}

/// <summary>User-maintained prices from <c>.taskrail/prices.yaml</c>. No prices are built in; they go stale.</summary>
public sealed class PriceTable
{
    private const decimal PerMillion = 1_000_000m;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private readonly Dictionary<string, ModelPrice> _prices;

    public PriceTable(IDictionary<string, ModelPrice> prices) =>
        _prices = new Dictionary<string, ModelPrice>(prices, StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Returns the cost in USD, or null when the model has no configured price.</summary>
    public decimal? Cost(string model, long inputTokens, long outputTokens, long cachedTokens)
    {
        if (!_prices.TryGetValue(model, out var p))
        {
            return null;
        }

        var cached = Math.Clamp(cachedTokens, 0, inputTokens);
        var uncached = inputTokens - cached;
        return ((uncached * p.Input) + (cached * (p.CachedInput ?? p.Input)) + (outputTokens * p.Output)) / PerMillion;
    }

    private sealed class PricesFile
    {
        public Dictionary<string, ModelPrice> Models { get; set; } = [];
    }
}
