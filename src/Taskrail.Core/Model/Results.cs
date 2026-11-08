namespace Taskrail.Core.Model;

/// <summary>What a worker is told for one attempt. Rendered into the executor's native brief file.</summary>
public sealed record Brief
{
    /// <summary>Short instruction passed on the executor command line.</summary>
    public required string Prompt { get; init; }

    /// <summary>Full brief written to the executor's native instructions file.</summary>
    public required string Body { get; init; }
}

public sealed record RunResult
{
    public required int ExitCode { get; init; }
    public required TimeSpan Duration { get; init; }
    public bool TimedOut { get; init; }
    public string StdoutPath { get; init; } = "";
    public string StderrPath { get; init; } = "";
}

public enum GateStatus
{
    Passed,
    Failed,

    /// <summary>Failed, but also failed on the base commit with no new failures detected. Non-blocking.</summary>
    PreExisting,
}

public sealed record GateResult
{
    public required string Gate { get; init; }
    public required GateStatus Status { get; init; }
    public bool? BaselinePassed { get; init; }
    public IReadOnlyList<string> NewFailures { get; init; } = [];
    public string Output { get; init; } = "";

    public bool Blocking => Status == GateStatus.Failed;
}

public enum DecisionKind
{
    Accept,
    Retry,
    Takeover,
    Split,
    Fail,
}

public sealed record Decision(DecisionKind Kind, string Reason);
