using Tokonomy.Core.Config;
using Tokonomy.Core.Model;

namespace Tokonomy.Core.Abstractions;

public interface IExecutor
{
    string Name { get; }
    ExecutorCapabilities Capabilities { get; }
    Task<RunResult> RunAsync(Brief brief, string workdir, string? model, TimeSpan timeout, CancellationToken ct);
}

public interface IGate
{
    string Name { get; }
    Task<GateResult> RunAsync(string workdir, Contract contract, CancellationToken ct);
}

public interface ISupervisor
{
    Decision Decide(UnitState unit, RunResult run, IReadOnlyList<GateResult> gates, Policy policy);
}

/// <summary>Per-unit state tracked by the orchestrator and read by the supervisor.</summary>
public sealed class UnitState
{
    public required WorkUnit Unit { get; init; }
    public int Attempts { get; set; }
    public int Takeovers { get; set; }
    public IReadOnlyList<string> ChangedPaths { get; set; } = [];
    public int DiffLines { get; set; }
    public bool AddedDependency { get; set; }
}

/// <summary>Chat access to the architect model. Implemented over an OpenAI-compatible endpoint or the Claude Code CLI.</summary>
public interface IProvider
{
    Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct);
}

public sealed record ProviderRequest(string Model, string System, string User, bool JsonOutput = false);

/// <summary>A completed architect call. <see cref="Usd"/> is null when the model has no configured price.</summary>
public sealed record ProviderResponse(
    string Text, string Model, long InputTokens, long OutputTokens, long CachedTokens, decimal? Usd, bool Estimated = false);

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
