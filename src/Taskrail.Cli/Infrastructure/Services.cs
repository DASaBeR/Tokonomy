using Taskrail.Core.Abstractions;
using Taskrail.Llm.Providers;
using Taskrail.Runtime.Config;
using Taskrail.Runtime.Executors;

namespace Taskrail.Cli.Infrastructure;

/// <summary>Swappable dependencies. Tests replace the provider and executor; production uses the defaults.</summary>
public sealed class Services
{
    public Func<TaskrailConfig, PriceTable, IProvider> ProviderFactory { get; init; } =
        (config, prices) => new OpenAiCompatibleProvider(config.Policy.Provider, prices);

    /// <summary>When set, replaces the configured executor and disables the metering proxy.</summary>
    public Func<ExecutorSession, IExecutor>? ExecutorFactory { get; init; }

    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    public static PriceTable LoadPrices(TaskrailConfig config) =>
        PriceTable.Load(Path.Combine(config.ConfigDirectory, TaskrailConfig.PricesFile));
}
