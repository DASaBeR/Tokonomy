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

    /// <summary>The packages behind <see cref="AddedDependency"/> that are not pre-approved, for the message shown to the user.</summary>
    public IReadOnlyList<string> AddedDependencyNames { get; set; } = [];
}

/// <summary>Chat access to the architect model. Implemented over an OpenAI-compatible endpoint or the Claude Code CLI.</summary>
public interface IProvider
{
    Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct);
}

public sealed record ProviderRequest(string Model, string System, string User, bool JsonOutput = false)
{
    /// <summary>Optional live progress from a provider that streams its answer (the Claude Code CLI does). Called on a background thread.</summary>
    public Action<ProviderProgress>? OnProgress { get; init; }
}

/// <summary>What a streaming provider has produced so far. <see cref="Retrying"/> is true while it redoes a call whose connection went silent.</summary>
public sealed record ProviderProgress(long OutputChars, bool Retrying = false)
{
    /// <summary>A rough token count (about 4 characters each), enough for a progress display.</summary>
    public long ApproxTokens => OutputChars / 4;
}

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
