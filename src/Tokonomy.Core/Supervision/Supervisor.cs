using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;

namespace Tokonomy.Core.Supervision;

/// <summary>
/// Deterministic policy. Rules are checked in a fixed order; the first match decides.
/// <list type="number">
/// <item>Diff larger than <c>max_diff_lines</c> → Split (the unit is mis-sized; cheaper than a takeover).</item>
/// <item>Diff touches a protected path → Takeover.</item>
/// <item>New dependency added and <c>review_on_new_dependency</c> → Takeover.</item>
/// <item>Worker timed out → Takeover.</item>
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

        if (unit.DiffLines > esc.MaxDiffLines)
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
            return new(DecisionKind.Takeover, "a new dependency was added", TakeoverTrigger.NewDependency);
        }

        if (run.TimedOut)
        {
            return new(DecisionKind.Takeover, "worker timed out", TakeoverTrigger.Timeout);
        }

        // unit.Attempts counts the attempt just evaluated.
        var limitReached = unit.Attempts >= Math.Max(1, esc.MaxRetriesPerUnit);

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

        return new(DecisionKind.Accept, gates.Count == 0 ? "worker finished (no gates)" : "all gates passed");
    }
}
