using Tokonomy.Core.Takeover;

namespace Tokonomy.Runtime.Orchestration;

/// <summary>
/// What the orchestrator was doing, saved atomically to <c>run-state.json</c> at every checkpoint. Together with
/// <c>plan-state.json</c> (the plan), the ledger (attempts, gates, calls, interventions, reviews) and the run branch
/// (one commit per accepted unit) it is everything <c>run --resume</c> needs. It never decides on its own that a unit
/// is complete: the branch is the evidence for that.
/// </summary>
public sealed record RunState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public required string RunId { get; init; }

    /// <summary>idle (between units), unit (a worker attempt or its gates), supervision, review, takeover, decomposition, finished.</summary>
    public required string Phase { get; init; }

    public string? CurrentUnit { get; init; }

    /// <summary>The branch commit the current unit started from; everything after it is unverified.</summary>
    public string? UnitStart { get; init; }

    public int PlanRevision { get; init; }

    /// <summary>Text the architect wrote in takeovers; enforced on every later unit.</summary>
    public IReadOnlyList<LockedRegion> Locks { get; init; } = [];

    public IReadOnlyList<Handoff> Handoffs { get; init; } = [];
    public int RunInterventions { get; init; }
    public int Splits { get; init; }
    public DateTimeOffset Updated { get; init; }

    /// <summary>How many times this run was resumed.</summary>
    public int Resumes { get; init; }
}

public static class RunPhases
{
    public const string Idle = "idle";
    public const string Unit = "unit";
    public const string Supervision = "supervision";
    public const string Review = "review";
    public const string Takeover = "takeover";
    public const string Decomposition = "decomposition";
    public const string Finished = "finished";
}

/// <summary>Another process holds the run (it is still executing).</summary>
public sealed class RunInProgressException(string message) : InvalidOperationException(message);
