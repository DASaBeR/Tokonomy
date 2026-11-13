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

    /// <summary>LLM usage the executor itself reported (see <c>usage_format</c>); empty when it reports none.</summary>
    public IReadOnlyList<WorkerUsage> Usage { get; init; } = [];

    /// <summary>
    /// Set when the executor ran but the run cannot be trusted as a finished implementation: a non-zero exit, an error
    /// status, a tool call the permission policy auto-denied, or a different model than the one requested.
    /// The supervisor never accepts a faulted run, whatever the gates say.
    /// </summary>
    public WorkerFault? Fault { get; init; }

    /// <summary>The model the executor says it actually used, when it reports one.</summary>
    public string? ReportedModel { get; init; }

    /// <summary>Execution metadata the executor reported (conversation id, shell commands run, denied actions).</summary>
    public ExecutorAudit? Audit { get; init; }
}

public enum WorkerFaultKind
{
    /// <summary>The process exited with a non-zero code.</summary>
    ProcessFailed,

    /// <summary>The process exited 0 but its own result says it failed (error status).</summary>
    ReportedError,

    /// <summary>A tool call was auto-denied because headless mode cannot prompt; the session ended early.</summary>
    PermissionDenied,

    /// <summary>The executor used a different model than requested. Never retried and never papered over.</summary>
    ModelMismatch,

    /// <summary>No parsable result at all: nothing proves the run completed.</summary>
    NoResult,

    /// <summary>
    /// The worktree was tampered with in a way no legitimate edit needs: a symlink, junction or hard link appeared, the
    /// <c>.git</c> pointer was changed, or a file Tokonomy manages was replaced. Never retried and never taken over.
    /// </summary>
    WorkspaceViolation,
}

public sealed record WorkerFault(WorkerFaultKind Kind, string Detail)
{
    public override string ToString() => $"{Kind}: {Detail}";
}

/// <summary>What an executor reported about one run, kept for the audit trail.</summary>
public sealed record ExecutorAudit
{
    public string? ConversationId { get; init; }
    public string? Status { get; init; }

    /// <summary>Shell commands the agent actually ran (its own tool calls, in order).</summary>
    public IReadOnlyList<string> Commands { get; init; } = [];

    /// <summary>Tool calls the permission policy refused, e.g. <c>command: python -c ...</c>.</summary>
    public IReadOnlyList<string> Denied { get; init; } = [];

    /// <summary>Isolation caveats that applied to this run (for example "temp directory writable").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Tokens and cost an executor reported for one model during an attempt. <c>Usd</c> is null when unknown.</summary>
public sealed record WorkerUsage(string Model, long InputTokens, long OutputTokens, long CachedTokens, decimal? Usd);

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

public enum ReviewVerdict
{
    Approve,
    Concerns,

    /// <summary>The review could not be obtained (architect call failed or the reply was unreadable).</summary>
    Unavailable,
}

/// <summary>
/// The architect's read of the combined diff after every unit passed its gates. Advisory only: gates outrank opinions,
/// so a review never turns a passing run into a failing one; it surfaces concerns for the human who merges.
/// </summary>
public sealed record FinalReview(ReviewVerdict Verdict, string Summary, IReadOnlyList<string> Concerns, string Model = "");

public enum UnitReviewVerdict
{
    Approve,
    ApproveWithWarnings,
    RequestChanges,
    Blocked,
    HumanDecisionRequired,

    /// <summary>The review could not be obtained (architect call failed, budget exhausted, or the reply was unreadable).</summary>
    Unavailable,
}

/// <summary>One problem the architect found in a unit's diff.</summary>
public sealed record ReviewFinding(string Severity, string Category, string? File, string Description, string? Recommendation = null)
{
    public bool High => Severity.Equals("high", StringComparison.OrdinalIgnoreCase) || Severity.Equals("critical", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"[{Severity}/{Category}] {(File is null ? "" : File + ": ")}{Description}{(string.IsNullOrWhiteSpace(Recommendation) ? "" : $" -> {Recommendation}")}";
}

/// <summary>
/// The architect's review of one unit after its gates passed. It complements the gates and never overrides them: a
/// unit is reviewed only once every mandatory gate passed, and approval cannot accept a unit whose gates failed.
/// </summary>
public sealed record UnitReview(UnitReviewVerdict Verdict, string Summary, IReadOnlyList<ReviewFinding> Findings, string Model = "")
{
    public bool Accepts => Verdict is UnitReviewVerdict.Approve or UnitReviewVerdict.ApproveWithWarnings;
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

    /// <summary>The worker run itself faulted (crash, error status, denied tool call) on every attempt.</summary>
    WorkerFault,
}

public sealed record Decision(DecisionKind Kind, string Reason, TakeoverTrigger Trigger = TakeoverTrigger.None)
{
    /// <summary>True when an architect edit can fix the problem (as opposed to needing a human).</summary>
    public bool CanTakeOver => Kind == DecisionKind.Takeover && Trigger is TakeoverTrigger.GateFailures or TakeoverTrigger.NoChanges or TakeoverTrigger.Timeout or TakeoverTrigger.WorkerFault;
}
