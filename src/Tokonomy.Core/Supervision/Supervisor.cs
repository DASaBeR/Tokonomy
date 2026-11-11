using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;

namespace Tokonomy.Core.Supervision;

/// <summary>
/// Deterministic policy. Rules are checked in a fixed order; the first match decides.
/// <list type="number">
/// <item>Worker used the wrong model → Fail (never retried, never taken over).</item>
/// <item>Workspace violation (links, <c>.git</c> tampering) → Fail (never retried, never taken over).</item>
/// <item>Diff larger than <c>max_diff_lines</c> and not verified by any passing gate → Split (the unit is mis-sized; cheaper than a takeover). A large diff that every gate accepted is kept.</item>
/// <item>Diff touches a protected path → Takeover.</item>
/// <item>New dependency added and <c>review_on_new_dependency</c> → Takeover.</item>
/// <item>Worker timed out → Takeover.</item>
/// <item>Worker run faulted (crash, error status, denied tool call) → Retry with the fault as feedback, Takeover once
/// failures reach the limit. A faulted run is never accepted, whatever the gates say.</item>
/// <item>No changes → Retry (or Takeover once failures reach the limit).</item>
/// <item>A gate has new failures → Retry with feedback, Takeover once failures reach <c>max_retries_per_unit</c>.</item>
/// <item>Otherwise → Accept.</item>
/// </list>
/// The orchestrator maps Takeover/Split to Fail when the strategy has no architect.
/// </summary>
public sealed class Supervisor : ISupervisor
{
    /// <summary>Always protected: a worker must never rewrite the policy that judges it.</summary>
    public static readonly IReadOnlyList<string> BuiltInProtectedPaths = [".tokonomy/**"];

    public Decision Decide(UnitState unit, RunResult run, IReadOnlyList<GateResult> gates, Policy policy)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(policy);
        var esc = policy.Escalation;

        // A different model than requested is a configuration problem, not a flaky attempt: never retry it and never
        // hand it to the architect as if the worker had tried. Whatever it wrote was not written by the model we asked for.
        if (run.Fault is { Kind: WorkerFaultKind.ModelMismatch } mismatch)
        {
            return new(DecisionKind.Fail, $"worker used the wrong model: {mismatch.Detail}");
        }

        // Links, a rewritten .git pointer or a replaced managed file are attempts to reach outside the worktree. Nothing
        // from such an attempt is trusted, retried or handed to the architect to build on.
        if (run.Fault is { Kind: WorkerFaultKind.WorkspaceViolation } violation)
        {
            return new(DecisionKind.Fail, $"workspace violation: {violation.Detail}");
        }

        // A large diff is a size guideline, not a defect: splitting a unit is not implemented, so rejecting a big change
        // that every gate (the architect's acceptance tests included) has verified would only throw working code away.
        // It is rejected only when nothing verified it: no gate ran, a gate failed, or the run faulted or timed out.
        var oversized = unit.DiffLines > esc.MaxDiffLines;
        var verified = gates.Count > 0 && gates.All(g => !g.Blocking) && run.Fault is null && !run.TimedOut;
        if (oversized && !verified)
        {
            return new(DecisionKind.Split, $"diff has {unit.DiffLines} changed lines (limit {esc.MaxDiffLines})");
        }

        var protectedHits = unit.ChangedPaths
            .Where(p => Glob.AnyMatch(BuiltInProtectedPaths, p) || Glob.AnyMatch(esc.ProtectedPaths, p))
            .ToList();
        if (protectedHits.Count > 0)
        {
            return new(DecisionKind.Takeover, $"diff touches protected path(s): {string.Join(", ", protectedHits.Take(5))}", TakeoverTrigger.ProtectedPath);
        }

        if (unit.AddedDependency && esc.ReviewOnNewDependency)
        {
            var named = unit.AddedDependencyNames.Count > 0 ? $": {string.Join(", ", unit.AddedDependencyNames.Take(8))}" : "";
            return new(DecisionKind.Takeover, $"a new dependency was added{named}", TakeoverTrigger.NewDependency);
        }

        if (run.TimedOut)
        {
            return new(DecisionKind.Takeover, "worker timed out", TakeoverTrigger.Timeout);
        }

        // unit.Attempts counts the attempt just evaluated.
        var limitReached = unit.Attempts >= Math.Max(1, esc.MaxRetriesPerUnit);

        // A faulted run is never accepted, even if the diff happens to pass every gate: the worker did not finish
        // cleanly (crash, error status, denied tool call), so nothing it produced is a reported success.
        if (run.Fault is { } fault)
        {
            var reason = $"worker run failed ({fault.Kind}): {fault.Detail}";
            return limitReached
                ? new(DecisionKind.Takeover, $"{reason} (failed {unit.Attempts} time(s))", TakeoverTrigger.WorkerFault)
                : new(DecisionKind.Retry, reason);
        }

        if (unit.ChangedPaths.Count == 0)
        {
            return limitReached
                ? new(DecisionKind.Takeover, $"worker made no changes in {unit.Attempts} attempt(s)", TakeoverTrigger.NoChanges)
                : new(DecisionKind.Retry, "worker made no changes");
        }

        var failing = gates.Where(g => g.Blocking).Select(g => g.Gate).ToList();
        if (failing.Count > 0)
        {
            var reason = $"gate(s) failed: {string.Join(", ", failing)}";
            return limitReached
                ? new(DecisionKind.Takeover, $"{reason} (failed {unit.Attempts} time(s))", TakeoverTrigger.GateFailures)
                : new(DecisionKind.Retry, reason);
        }

        return new(DecisionKind.Accept, gates.Count == 0
            ? "worker finished (no gates)"
            : oversized ? $"all gates passed (large change: {unit.DiffLines} lines, over the {esc.MaxDiffLines}-line guideline)" : "all gates passed");
    }
}
