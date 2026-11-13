namespace Tokonomy.Core.Model;

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

    /// <summary>
    /// Why the call was made, for cost attribution: planning, inspection, critique, revision, supervision, takeover,
    /// review, decomposition, final_review (architect), or null for worker calls and rows written by older builds.
    /// </summary>
    public string? Purpose { get; init; }
}

/// <summary>Architect call purposes recorded in the ledger (planning purposes come from the planner).</summary>
public static class CallPurposes
{
    public const string Supervision = "supervision";
    public const string Takeover = "takeover";
    public const string Review = "review";
    public const string Decomposition = "decomposition";
    public const string FinalReview = "final_review";
}

/// <summary>One architect intervention, traceable from trigger to outcome.</summary>
public sealed record InterventionRecord
{
    public required string RunId { get; init; }
    public required string UnitId { get; init; }
    public required DateTimeOffset At { get; init; }

    /// <summary>The deterministic trigger (worker_blocked, retry_limit, ...).</summary>
    public required string Trigger { get; init; }

    /// <summary>0 no action, 1 instruction correction, 2 guided correction, 3 takeover, -1 no usable decision.</summary>
    public required int Level { get; init; }

    public string RootCause { get; init; } = "";
    public string Diagnosis { get; init; } = "";

    /// <summary>What was done: instructions given, edits applied, plan updated, worktree reset.</summary>
    public string Action { get; init; } = "";

    /// <summary>resumed, accepted, failed, human, no_action, rejected.</summary>
    public required string Outcome { get; init; }

    /// <summary>The plan revision this intervention produced, if it changed the plan.</summary>
    public int? PlanRevision { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];
    public decimal Usd { get; init; }
}

/// <summary>A plan revision as recorded in the ledger (the full before/after values live in plan-state.json).</summary>
public sealed record PlanRevisionRecord(
    string RunId, int Revision, DateTimeOffset At, string Source, string Reason, string? Trigger, int Changes, bool RequiresApproval);

public sealed record UnitReviewRecord(
    string RunId, string UnitId, DateTimeOffset At, UnitReviewVerdict Verdict, string Summary, string Why, IReadOnlyList<ReviewFinding> Findings);

/// <summary>Architect or worker spend for one purpose.</summary>
public sealed record PurposeTotals(LlmRole Role, string Purpose, int Calls, long InputTokens, long OutputTokens, decimal Usd, bool AnyEstimated);

/// <summary>Supervision statistics of one run, for reports and the benchmark.</summary>
public sealed record RunStats
{
    public int Units { get; init; }
    public int UnitsAccepted { get; init; }

    /// <summary>Accepted units the architect never had to take over (L3).</summary>
    public int UnitsAcceptedWithoutTakeover { get; init; }

    public int WorkerAttempts { get; init; }

    /// <summary>Worker attempts beyond the first per unit.</summary>
    public int WorkerRetries { get; init; }

    public int Takeovers { get; init; }
    public int Interventions { get; init; }
    public int InstructionCorrections { get; init; }
    public int GuidedCorrections { get; init; }
    public int PlanRevisions { get; init; }
    public int ReviewChangeRequests { get; init; }

    /// <summary>High-severity architectural findings across all unit reviews.</summary>
    public int ArchitecturalViolations { get; init; }

    /// <summary>Gate failures of the lessons check: a corrected mistake made again.</summary>
    public int RepeatedMistakes { get; init; }

    /// <summary>Times the run stopped for a human decision.</summary>
    public int HumanDecisions { get; init; }

    /// <summary>Worker reports that asked the architect for a decision (blocked, clarification, decision required).</summary>
    public int BlockerReports { get; init; }

    /// <summary>Architect takeovers (classic or L3) whose edit did not pass the gates.</summary>
    public int FailedTakeovers { get; init; }

    /// <summary>Units the architect split into smaller units.</summary>
    public int Splits { get; init; }

    /// <summary>Lessons recorded during the run.</summary>
    public int Lessons { get; init; }

    /// <summary>Architect interventions at level 3 (supervised takeovers).</summary>
    public int SupervisedTakeovers { get; init; }

    /// <summary>Spend by role and purpose, e.g. "architect:planning", "worker:work".</summary>
    public IReadOnlyDictionary<string, decimal> UsdByPurpose { get; init; } = new Dictionary<string, decimal>();

    /// <summary>Benchmark only: files the final branch changed that no unit of the final plan named (tests excluded). Null when not measured.</summary>
    public int? UnplannedChanges { get; init; }

    /// <summary>Benchmark only: the independent judge's verdict on the final diff (Approve, Concerns, Unavailable). Null when not judged.</summary>
    public string? JudgeVerdict { get; init; }

    /// <summary>Benchmark only: the judge's concerns, kept for the analysis.</summary>
    public IReadOnlyList<string> JudgeConcerns { get; init; } = [];
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

    /// <summary>Token use of every model that worked on this trial (read from the run's ledger rows, not stored on the trial).</summary>
    public IReadOnlyList<ModelUsage> Models { get; init; } = [];

    /// <summary>Supervision statistics of the trial's run (read from the ledger, not stored on the trial).</summary>
    public RunStats Stats { get; init; } = new();

    /// <summary>Fresh (uncached) prompt tokens. Cache reads are counted separately in <see cref="CachedTokens"/>.</summary>
    public long InputTokens => Models.Sum(m => m.InputTokens);

    public long OutputTokens => Models.Sum(m => m.OutputTokens);

    /// <summary>Prompt tokens served from a cache (billed at a lower rate); not part of <see cref="InputTokens"/>.</summary>
    public long CachedTokens => Models.Sum(m => m.CachedTokens);

    /// <summary>Fresh input plus output: the tokens that were actually processed and generated, cache reads left out.</summary>
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>What one model used in one run: calls, tokens and cost.</summary>
public sealed record ModelUsage(string Model, int Calls, long InputTokens, long OutputTokens, long CachedTokens, decimal Usd);

/// <summary>Aggregated view used by <c>tokonomy report</c>.</summary>
public sealed record RunSummary
{
    public required RunRecord Run { get; init; }
    public required IReadOnlyList<TierTotals> Tiers { get; init; }

    /// <summary>Spend split by role and purpose.</summary>
    public IReadOnlyList<PurposeTotals> Purposes { get; init; } = [];

    public RunStats Stats { get; init; } = new();
    public IReadOnlyList<InterventionRecord> Interventions { get; init; } = [];
    public IReadOnlyList<PlanRevisionRecord> Revisions { get; init; } = [];
    public IReadOnlyList<UnitReviewRecord> Reviews { get; init; } = [];
}

public sealed record TierTotals(LlmRole Role, int Calls, long InputTokens, long OutputTokens, decimal Usd, bool AnyEstimated);
