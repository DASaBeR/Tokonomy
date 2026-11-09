using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Process;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Cli.Infrastructure;

/// <summary>Swappable dependencies. Tests replace the provider and executor; production uses the defaults.</summary>
public sealed class Services
{
    public Func<TokonomyConfig, PriceTable, IProvider> ProviderFactory { get; init; } = CreateProvider;

    /// <summary>When set, replaces the configured executor and disables the metering proxy.</summary>
    public Func<ExecutorSession, IExecutor>? ExecutorFactory { get; init; }

    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    private static IProvider CreateProvider(TokonomyConfig config, PriceTable prices) =>
        config.Policy.ArchitectProvider.Kind switch
        {
            ArchitectProviderConfig.ClaudeCliKind => new ClaudeCliProvider(new ProcessRunner(), config.Policy.ArchitectProvider.Command),
            _ => new OpenAiCompatibleProvider(config.Policy.Provider, prices),
        };

    public static PriceTable LoadPrices(TokonomyConfig config) =>
        PriceTable.Load(Path.Combine(config.ConfigDirectory, TokonomyConfig.PricesFile));
}
