namespace Taskrail.Core.Model;

public enum RunStatus
{
    Planned,
    Running,
    Pass,
    Fail,
    AbortedBudget,
    Error,
}

public enum CostSource
{
    Metered,
    Estimated,
}

public enum LlmRole
{
    Architect,
    Worker,
}

public sealed record RunRecord
{
    public required string Id { get; init; }
    public required string Strategy { get; init; }
    public required string TaskId { get; init; }
    public required string Repo { get; init; }
    public string BaseSha { get; init; } = "";
    public RunStatus Status { get; init; } = RunStatus.Running;
    public required DateTimeOffset Started { get; init; }
    public DateTimeOffset? Ended { get; init; }
    public decimal TotalUsd { get; init; }
    public CostSource CostSource { get; init; } = CostSource.Metered;
}

public sealed record LlmCallRecord
{
    public required string RunId { get; init; }
    public string? UnitId { get; init; }
    public required LlmRole Role { get; init; }
    public required string Model { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CachedTokens { get; init; }
    public decimal Usd { get; init; }
    public CostSource Source { get; init; } = CostSource.Metered;
}

public sealed record AttemptRecord
{
    public required string RunId { get; init; }
    public required string UnitId { get; init; }
    public required int Number { get; init; }
    public required string Executor { get; init; }
    public string? Model { get; init; }
    public int ExitCode { get; init; }
    public TimeSpan Duration { get; init; }
    public int DiffLines { get; init; }
    public required DecisionKind Decision { get; init; }
    public required string Reason { get; init; }
}

/// <summary>One benchmark trial: a task run under one strategy, judged by hidden acceptance checks.</summary>
public sealed record BenchResultRecord
{
    public required string BenchId { get; init; }
    public required string TaskId { get; init; }
    public required string Strategy { get; init; }
    public required int Repeat { get; init; }
    public required string RunId { get; init; }
    public required RunStatus RunStatus { get; init; }

    /// <summary>Passed the hidden acceptance check. This, not the run status, defines success.</summary>
    public required bool Accepted { get; init; }

    public decimal Usd { get; init; }
    public CostSource CostSource { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Note { get; init; }
}

/// <summary>Aggregated view used by <c>taskrail report</c>.</summary>
public sealed record RunSummary
{
    public required RunRecord Run { get; init; }
    public required IReadOnlyList<TierTotals> Tiers { get; init; }
}

public sealed record TierTotals(LlmRole Role, int Calls, long InputTokens, long OutputTokens, decimal Usd, bool AnyEstimated);
