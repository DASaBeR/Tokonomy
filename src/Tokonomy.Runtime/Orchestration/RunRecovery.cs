using System.Text.Json;
using System.Text.RegularExpressions;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;

namespace Tokonomy.Runtime.Orchestration;

/// <summary>What <c>run --resume</c> hands the orchestrator: the saved state as loaded, with load errors kept, not hidden.</summary>
public sealed record ResumeRequest
{
    public PlanState? PlanState { get; init; }

    /// <summary>Why <c>plan-state.json</c> could not be read (corrupt or truncated), if it could not.</summary>
    public string? PlanStateError { get; init; }

    public RunState? RunState { get; init; }
    public string? RunStateError { get; init; }
}

/// <summary>Per-unit history read from the ledger, so caps (retries, takeovers, interventions) survive a restart.</summary>
public sealed record UnitHistory(int Attempts, int Takeovers, int Interventions, bool InstructionCorrectionUsed);

/// <summary>The reconciled state a resumed run continues from, or why it must not continue.</summary>
public sealed record RecoveryDecision
{
    public bool Safe => Problems.Count == 0;

    /// <summary>Reasons the run must not be resumed automatically (each needs a human).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>What reconciliation found and did, for the user and the transcript.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public PlanState? Plan { get; init; }
    public RunState? Run { get; init; }
    public IReadOnlyDictionary<string, UnitHistory> History { get; init; } = new Dictionary<string, UnitHistory>();

    /// <summary>Units whose acceptance tests are already committed on the branch (not committed again).</summary>
    public IReadOnlySet<string> TestsCommitted { get; init; } = new HashSet<string>();

    /// <summary>Plan revisions that are in plan-state.json but missing from the ledger (re-recorded on resume).</summary>
    public IReadOnlyList<PlanRevision> RevisionsToRecord { get; init; } = [];
}

/// <summary>
/// Decides whether and how an interrupted run continues. The run branch is the only evidence that a unit is complete:
/// a commit <c>tokonomy(&lt;id&gt;): ...</c> exists only after the unit's gates (and review) passed. Saved flags are
/// checked against it, never trusted on their own. Anything ambiguous is refused, with the reason, instead of guessed.
/// </summary>
public static partial class RunRecovery
{
    public static RecoveryDecision Reconcile(
        string runId, Contract planned, ResumeRequest request, IReadOnlyList<(string Sha, string Subject)> commits,
        IReadOnlyList<AttemptRecord> attempts, IReadOnlyList<InterventionRecord> interventions, IReadOnlyList<PlanRevisionRecord> ledgerRevisions)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<string>();
        var notes = new List<string>();

        // 1. Evidence on the branch.
        var completed = new List<string>();
        var testsCommitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (sha, subject) in commits)
        {
            var m = UnitCommit().Match(subject);
            if (!m.Success)
            {
                problems.Add($"commit {sha[..Math.Min(8, sha.Length)]} on the run branch was not made by Tokonomy (\"{subject}\"); the branch was changed by hand");
                continue;
            }

            var unitId = m.Groups[1].Value;
            if (m.Groups[2].Value == "acceptance tests")
            {
                testsCommitted.Add(unitId);
            }
            else if (!completed.Contains(unitId))
            {
                completed.Add(unitId);
            }
            else
            {
                problems.Add($"unit {unitId} was committed twice on the run branch");
            }
        }

        // 2. The plan state.
        PlanState state;
        if (request.PlanStateError is { } loadError)
        {
            return Refuse($"plan-state.json is unreadable ({loadError}); restore it from a backup or start again with `run --plan {runId} --again`");
        }

        if (request.PlanState is { } saved)
        {
            state = saved;
            if (!SameContract(state.Original, planned))
            {
                problems.Add("plan-state.json belongs to a different plan than plan.json (the original plans differ)");
            }

            if (ContractValidator.Validate(state.Current) is { Count: > 0 } invalid)
            {
                problems.Add($"the saved current plan is invalid: {string.Join("; ", invalid)}");
            }

            if (state.Revision != 1 + state.Revisions.Count || state.Revisions.Select((r, i) => r.Number != i + 2).Any(bad => bad))
            {
                problems.Add($"the saved revision history is inconsistent (revision {state.Revision}, {state.Revisions.Count} recorded change(s))");
            }

            foreach (var unit in state.Current.Units.Where(u => !state.Progress.ContainsKey(u.Id)))
            {
                problems.Add($"unit {unit.Id} has no saved progress");
            }
        }
        else if (completed.Count > 0)
        {
            return Refuse("units were completed but plan-state.json is missing, so the plan they followed is unknown");
        }
        else
        {
            state = new PlanManager(planned).Snapshot();
            notes.Add("no plan state was saved yet; resuming from the original plan");
        }

        if (problems.Count > 0)
        {
            return new RecoveryDecision { Problems = problems, Notes = notes };
        }

        // 3. Reconcile unit progress with the evidence.
        var progress = new Dictionary<string, UnitProgress>(state.Progress, StringComparer.Ordinal);
        foreach (var id in completed)
        {
            var known = progress.TryGetValue(id, out var p);
            if (!known || !state.Current.Units.Any(u => u.Id == id))
            {
                problems.Add($"unit {id} is committed on the branch but is not part of the saved current plan");
                continue;
            }

            switch (p)
            {
                case UnitProgress.Accepted:
                    break;
                case UnitProgress.Pending or UnitProgress.Running:
                    progress[id] = UnitProgress.Accepted;
                    notes.Add($"unit {id} was committed (it passed its gates) just before the interruption; marked accepted");
                    break;
                default:
                    problems.Add($"unit {id} is committed on the branch but saved as {p.ToString().ToLowerInvariant()}");
                    break;
            }
        }

        foreach (var (id, p) in progress.ToList())
        {
            if (p == UnitProgress.Accepted && !completed.Contains(id))
            {
                progress[id] = UnitProgress.Pending;
                notes.Add($"unit {id} was saved as accepted but its commit is not on the branch; it will run again");
            }
            else if (p == UnitProgress.Running)
            {
                progress[id] = UnitProgress.Pending;
                notes.Add($"unit {id} was interrupted; it restarts from its last verified commit");
            }
            else if (p == UnitProgress.Failed)
            {
                problems.Add($"unit {id} already failed its gates or its escalation; that is a verdict, not an interruption (use `run --plan {runId} --again`)");
            }
        }

        // Dependencies of an accepted unit must be accepted too, or the branch does not match the plan's order.
        foreach (var unit in state.Current.Units.Where(u => progress.GetValueOrDefault(u.Id) == UnitProgress.Accepted))
        {
            foreach (var dep in unit.DependsOn.Where(d => progress.GetValueOrDefault(d) != UnitProgress.Accepted))
            {
                problems.Add($"unit {unit.Id} is committed but its dependency {dep} is not");
            }
        }

        if (problems.Count > 0)
        {
            return new RecoveryDecision { Problems = problems, Notes = notes };
        }

        // 4. The checkpoint.
        RunState run;
        if (request.RunStateError is { } runError)
        {
            if (completed.Count > 0 && LocksMayExist(attempts))
            {
                return Refuse($"run-state.json is unreadable ({runError}) and an architect takeover happened, so its locked regions cannot be restored");
            }

            notes.Add($"run-state.json is unreadable ({runError}); continuing without it (no takeover locks to restore)");
            run = new RunState { RunId = runId, Phase = RunPhases.Idle };
        }
        else if (request.RunState is { } checkpoint)
        {
            if (checkpoint.RunId != runId)
            {
                return Refuse($"run-state.json belongs to run {checkpoint.RunId}");
            }

            run = checkpoint;
            if (checkpoint.Phase is not (RunPhases.Idle or RunPhases.Finished))
            {
                notes.Add($"interrupted during {checkpoint.Phase}{(checkpoint.CurrentUnit is null ? "" : " of " + checkpoint.CurrentUnit)}: " +
                          "anything it changed after the unit's last commit is discarded (saved as a patch); a plan revision it completed is kept");
            }
        }
        else
        {
            if (completed.Count > 0 && LocksMayExist(attempts))
            {
                return Refuse("run-state.json is missing and an architect takeover happened, so its locked regions cannot be restored");
            }

            run = new RunState { RunId = runId, Phase = RunPhases.Idle };
        }

        // 5. Ledger and plan state agree on revisions (the state is written first, so the ledger can only lag).
        var recorded = ledgerRevisions.Select(r => r.Revision).ToHashSet();
        var missing = state.Revisions.Where(r => !recorded.Contains(r.Number)).ToList();
        if (missing.Count > 0)
        {
            notes.Add($"{missing.Count} plan revision(s) were saved but not yet in the ledger; recorded now");
        }

        foreach (var orphan in ledgerRevisions.Where(r => r.Revision > state.Revision))
        {
            notes.Add($"the ledger has revision {orphan.Revision}, which never reached plan-state.json; it is kept for audit but not applied");
        }

        // 6. Per-unit history, so caps hold across the restart.
        var history = attempts.GroupBy(a => a.UnitId, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => new UnitHistory(
                g.Where(a => a.Executor != "architect").Select(a => a.Number).DefaultIfEmpty(0).Max(),
                g.Count(a => a.Executor == "architect" && a.Reason.StartsWith("takeover", StringComparison.Ordinal)),
                interventions.Count(i => i.UnitId == g.Key),
                interventions.Any(i => i.UnitId == g.Key && i.Level == 1)),
            StringComparer.Ordinal);

        return new RecoveryDecision
        {
            Notes = notes,
            Plan = state with { Progress = progress },
            Run = run with { RunInterventions = Math.Max(run.RunInterventions, interventions.Count) },
            History = history,
            TestsCommitted = testsCommitted,
            RevisionsToRecord = missing,
        };

        RecoveryDecision Refuse(string reason) => new() { Problems = [.. problems, reason], Notes = notes };
    }

    private static bool LocksMayExist(IReadOnlyList<AttemptRecord> attempts) =>
        attempts.Any(a => a.Executor == "architect" && a.Reason.StartsWith("takeover: gates pass", StringComparison.Ordinal));

    private static bool SameContract(Contract a, Contract b) =>
        JsonSerializer.Serialize(a, RunArtifacts.JsonOptions) == JsonSerializer.Serialize(b, RunArtifacts.JsonOptions);

    [GeneratedRegex(@"^tokonomy\(([A-Za-z][A-Za-z0-9_-]{0,31})\): (.*)$")]
    private static partial Regex UnitCommit();
}
