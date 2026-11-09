namespace Tokonomy.Core.Model;

/// <summary>What a worker is told for one attempt. Rendered into the executor's native brief file.</summary>
public sealed record Brief
{
    /// <summary>Short instruction passed on the executor command line.</summary>
    public required string Prompt { get; init; }

    /// <summary>Full brief written to the executor's native instructions file.</summary>
    public required string Body { get; init; }

    /// <summary>Identifies the attempt (e.g. <c>U1-a2</c>); used for log file names.</summary>
    public string Label { get; init; } = "attempt";
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

/// <summary>Why the supervisor asked for an escalation. Only some triggers can be resolved by an architect edit.</summary>
public enum TakeoverTrigger
{
    None,

    /// <summary>A gate kept failing on the same unit.</summary>
    GateFailures,

    /// <summary>The worker finished without changing anything.</summary>
    NoChanges,

    /// <summary>The worker timed out, possibly leaving partial work.</summary>
    Timeout,

    /// <summary>The diff touches a protected path: a human decision, not something an edit resolves.</summary>
    ProtectedPath,

    /// <summary>The diff adds a dependency: a human decision.</summary>
    NewDependency,
}

public sealed record Decision(DecisionKind Kind, string Reason, TakeoverTrigger Trigger = TakeoverTrigger.None)
{
    /// <summary>True when an architect edit can fix the problem (as opposed to needing a human).</summary>
    public bool CanTakeOver => Kind == DecisionKind.Takeover && Trigger is TakeoverTrigger.GateFailures or TakeoverTrigger.NoChanges or TakeoverTrigger.Timeout;
}
