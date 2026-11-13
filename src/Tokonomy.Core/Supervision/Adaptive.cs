using System.Text;
using System.Text.RegularExpressions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;

namespace Tokonomy.Core.Supervision;

/// <summary>How far the architect steps in. Higher levels cost more and take more control away from the worker.</summary>
public enum InterventionLevel
{
    /// <summary>The architect looked and found nothing to change; the deterministic decision stands.</summary>
    None = 0,

    /// <summary>L1: the worker misunderstood; the architect corrects the instructions and the worker resumes.</summary>
    InstructionCorrection = 1,

    /// <summary>L2: the architect applies a small targeted fix, gates re-run, and the worker continues if needed.</summary>
    GuidedCorrection = 2,

    /// <summary>L3: the architect finishes the unit itself; its edit is verified, locked and handed off.</summary>
    Takeover = 3,
}

/// <summary>Why the architect is called. Each comes from a deterministic rule, never from a model.</summary>
public enum SupervisionTrigger
{
    WorkerBlocked,
    ClarificationRequired,
    ScopeDeviation,
    RepeatedFailure,
    RetryLimit,
    NoChanges,
    Timeout,
    WorkerFault,
    ReviewRejected,
}

/// <summary>A call for the architect: why, and which intervention levels policy allows for it.</summary>
public sealed record SupervisionRequest(SupervisionTrigger Trigger, string Reason, IReadOnlyList<InterventionLevel> AllowedLevels)
{
    public bool Allows(InterventionLevel level) => AllowedLevels.Contains(level);
}

/// <summary>What the intervention policy looks at after an attempt.</summary>
public sealed record SupervisionContext
{
    public required Decision Decision { get; init; }
    public WorkerReport? Report { get; init; }

    /// <summary>Changed paths the unit was told to avoid.</summary>
    public IReadOnlyList<string> AvoidTouched { get; init; } = [];

    /// <summary>The same gate failure as the previous attempt.</summary>
    public bool RepeatedFailure { get; init; }

    public int UnitInterventions { get; init; }
    public int RunInterventions { get; init; }
    public int Takeovers { get; init; }

    /// <summary>An instruction correction (L1) was already tried on this unit.</summary>
    public bool InstructionCorrectionUsed { get; init; }

    /// <summary>The review sent the unit back more often than allowed.</summary>
    public bool ReviewRejected { get; init; }
}

/// <summary>
/// Deterministic escalation rules: when to spend an architect call, and which intervention levels are allowed. Infra
/// failures (provider errors, sandbox provisioning) never reach here; they are errors, not worker mistakes.
/// </summary>
public static class InterventionPolicy
{
    public static SupervisionRequest? Evaluate(SupervisionContext ctx, SupervisionConfig supervision, EscalationConfig escalation)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(supervision);
        ArgumentNullException.ThrowIfNull(escalation);
        var decision = ctx.Decision;

        // Never for what must not be built on (wrong model, workspace violation) or what a human must decide.
        if (decision.Kind == DecisionKind.Fail ||
            decision.Trigger is TakeoverTrigger.ProtectedPath or TakeoverTrigger.NewDependency ||
            decision.Kind == DecisionKind.Split)
        {
            return null;
        }

        if (ctx.UnitInterventions >= supervision.MaxInterventionsPerUnit || ctx.RunInterventions >= supervision.MaxInterventionsPerRun)
        {
            return null;
        }

        var takeover = ctx.Takeovers < escalation.MaxTakeoversPerUnit;
        var l1 = !ctx.InstructionCorrectionUsed;
        List<InterventionLevel> Levels(bool allowL1, bool allowL3)
        {
            var levels = new List<InterventionLevel> { InterventionLevel.None };
            if (allowL1)
            {
                levels.Add(InterventionLevel.InstructionCorrection);
            }

            levels.Add(InterventionLevel.GuidedCorrection);
            if (allowL3)
            {
                levels.Add(InterventionLevel.Takeover);
            }

            return levels;
        }

        if (ctx.ReviewRejected)
        {
            return new(SupervisionTrigger.ReviewRejected, "the architectural review keeps requesting changes", Levels(l1, takeover));
        }

        if (ctx.Report is { NeedsArchitect: true } report)
        {
            var trigger = report.Status == WorkerReportStatus.ClarificationRequired ? SupervisionTrigger.ClarificationRequired : SupervisionTrigger.WorkerBlocked;
            return new(trigger, $"the worker reported {report.Summary}", Levels(l1, takeover));
        }

        if (ctx.AvoidTouched.Count > 0 && decision.Kind is DecisionKind.Accept or DecisionKind.Retry)
        {
            return new(SupervisionTrigger.ScopeDeviation,
                $"the worker changed path(s) the unit must avoid: {string.Join(", ", ctx.AvoidTouched.Take(5))}", Levels(l1, allowL3: false));
        }

        if (decision.Kind == DecisionKind.Retry && ctx.RepeatedFailure && supervision.EarlyOnRepeatedFailure)
        {
            return new(SupervisionTrigger.RepeatedFailure, $"the same failure happened twice in a row: {decision.Reason}", Levels(l1, allowL3: false));
        }

        if (decision.Kind == DecisionKind.Takeover && decision.CanTakeOver)
        {
            var trigger = decision.Trigger switch
            {
                TakeoverTrigger.NoChanges => SupervisionTrigger.NoChanges,
                TakeoverTrigger.Timeout => SupervisionTrigger.Timeout,
                TakeoverTrigger.WorkerFault => SupervisionTrigger.WorkerFault,
                _ => SupervisionTrigger.RetryLimit,
            };
            return new(trigger, decision.Reason, Levels(l1, takeover));
        }

        return null;
    }
}

/// <summary>A short, stable fingerprint of an attempt's blocking gate failures, for spotting the same failure twice.</summary>
public static partial class FailureSignature
{
    public static string? Of(IReadOnlyList<GateResult> gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        var blocking = gates.Where(g => g.Blocking).ToList();
        if (blocking.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var gate in blocking.OrderBy(g => g.Gate, StringComparer.Ordinal))
        {
            sb.Append(gate.Gate).Append('|').AppendJoin(',', gate.NewFailures.Order(StringComparer.Ordinal)).Append('|');
            var tail = gate.Output.Length <= 600 ? gate.Output : gate.Output[^600..];
            // Durations, line numbers in stack traces and addresses vary between identical failures.
            sb.Append(Digits().Replace(tail, "#")).Append('\n');
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}

/// <summary>How a unit's diff deviates from its plan: avoided paths touched, unplanned files, planned files not created.</summary>
public sealed record PlanDeviation(IReadOnlyList<string> AvoidTouched, IReadOnlyList<string> Unplanned, IReadOnlyList<string> MissingCreates)
{
    public static readonly PlanDeviation None = new([], [], []);

    public bool Any => AvoidTouched.Count > 0 || Unplanned.Count > 0 || MissingCreates.Count > 0;

    public IEnumerable<string> Describe()
    {
        if (AvoidTouched.Count > 0)
        {
            yield return $"changed path(s) the unit must avoid: {string.Join(", ", AvoidTouched.Take(8))}";
        }

        if (Unplanned.Count > 0)
        {
            yield return $"changed file(s) the plan did not mention: {string.Join(", ", Unplanned.Take(8))}";
        }

        if (MissingCreates.Count > 0)
        {
            yield return $"did not create planned file(s): {string.Join(", ", MissingCreates.Take(8))}";
        }
    }

    /// <summary>Compares the changed paths with the unit's plan. Test files and the acceptance directory never count as unplanned.</summary>
    public static PlanDeviation Detect(WorkUnit unit, IReadOnlyList<string> changed, string? acceptanceDir = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(changed);
        var avoid = changed.Where(p => unit.Avoid.Any(a => PlanPaths.Covers(a, p))).ToList();
        var expected = unit.ExpectedFiles.Select(PlanPaths.Normalize).OfType<string>().ToList();
        var unplanned = expected.Count == 0
            ? []
            : changed.Where(p => !expected.Any(e => PlanPaths.IsGlob(e) ? Glob.IsMatch(e, p) : PlanPaths.Covers(e, p)) &&
                                 !IsTestPath(p) &&
                                 (acceptanceDir is null || !p.StartsWith(acceptanceDir.TrimEnd('/') + "/", StringComparison.Ordinal)) &&
                                 !avoid.Contains(p))
                .ToList();
        var missing = unit.Create.Select(PlanPaths.Normalize).OfType<string>().Where(c => !changed.Contains(c, StringComparer.Ordinal)).ToList();
        return new PlanDeviation(avoid, unplanned, missing);
    }

    private static bool IsTestPath(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Split('/').Any(s => s is "test" or "tests" or "spec" or "specs" or "__tests__") ||
               Path.GetFileName(lower) is var name && (name.StartsWith("test_", StringComparison.Ordinal) || name.Contains("test.", StringComparison.Ordinal) ||
                                                      name.Contains("tests.", StringComparison.Ordinal) || name.Contains(".spec.", StringComparison.Ordinal));
    }
}

/// <summary>What the risk-based review policy looks at.</summary>
public sealed record ReviewSignals
{
    public UnitRisk Risk { get; init; }
    public int DiffLines { get; init; }
    public int ChangedFiles { get; init; }
    public int Attempts { get; init; }
    public bool Intervened { get; init; }
    public bool ReportedDiscovery { get; init; }
    public bool DependencyChanged { get; init; }
    public PlanDeviation Deviation { get; init; } = PlanDeviation.None;
}

/// <summary>Decides, deterministically, whether a unit whose gates passed is worth an architect review.</summary>
public static class ReviewPolicy
{
    public static (bool Review, string Why) ShouldReview(ReviewConfig config, ReviewSignals s)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(s);
        switch (config.Mode)
        {
            case ReviewConfig.Off:
                return (false, "review.mode is off");
            case ReviewConfig.Always:
                return (true, "review.mode is always");
        }

        if (s.Risk == UnitRisk.High)
        {
            return (true, "high-risk unit");
        }

        if (s.Intervened)
        {
            return (true, "the architect intervened on this unit");
        }

        if (s.Deviation.AvoidTouched.Count > 0 || s.Deviation.Unplanned.Count > 0)
        {
            return (true, "the diff deviates from the plan");
        }

        if (s.ReportedDiscovery)
        {
            return (true, "the worker reported an unexpected discovery");
        }

        if (s.DependencyChanged)
        {
            return (true, "dependencies changed");
        }

        if (s.Risk == UnitRisk.Medium && (s.DiffLines >= config.MediumRiskDiffLines || s.Attempts > 1 || s.ChangedFiles > 4))
        {
            return (true, "medium-risk unit with a large diff or several attempts");
        }

        return (false, "routine unit with no warning signs");
    }

    /// <summary>
    /// Whether a change the architect wrote itself (and the gates passed) gets an independent review. Small fixes inside
    /// the plan do not: gates already verified them and a second call would mostly be the same model approving itself.
    /// </summary>
    public static (bool Review, string Why) ShouldReviewArchitectChange(ReviewConfig config, ReviewSignals s)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(s);
        if (config.Mode == ReviewConfig.Off || config.ArchitectChanges == ReviewConfig.Off)
        {
            return (false, "review of architect changes is off");
        }

        if (config.ArchitectChanges == ReviewConfig.Always)
        {
            return (true, "independent review of an architect-written change (always)");
        }

        if (s.Risk == UnitRisk.High)
        {
            return (true, "independent review: architect-written change on a high-risk unit");
        }

        if (s.Deviation.AvoidTouched.Count > 0 || s.Deviation.Unplanned.Count > 0)
        {
            return (true, "independent review: the architect's change goes outside the unit's plan");
        }

        return s.DiffLines >= 2 * config.MediumRiskDiffLines
            ? (true, "independent review: large architect-written change")
            : (false, "small architect change inside the plan, verified by the gates");
    }
}

/// <summary>
/// Enforces lessons that carry a forbidden pattern: no line a later unit adds may match it. It reads the diff only and
/// executes nothing, like the locked-region check.
/// </summary>
public static class LessonCheck
{
    public const string GateName = "lessons";

    public static IReadOnlyList<string> Violations(IReadOnlyList<Lesson> lessons, string patch)
    {
        ArgumentNullException.ThrowIfNull(lessons);
        ArgumentNullException.ThrowIfNull(patch);
        var enforced = lessons.Where(l => !string.IsNullOrWhiteSpace(l.ForbiddenPattern)).ToList();
        if (enforced.Count == 0 || patch.Length == 0)
        {
            return [];
        }

        var added = AddedLines(patch);
        var found = new List<string>();
        foreach (var lesson in enforced)
        {
            Regex regex;
            try
            {
                regex = new Regex(lesson.ForbiddenPattern!, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var (file, line) in added)
            {
                bool hit;
                try
                {
                    hit = regex.IsMatch(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }

                if (hit)
                {
                    var shown = line.Trim();
                    found.Add($"{lesson.Id} ({lesson.Text}): a line added to {file} matches the forbidden pattern /{lesson.ForbiddenPattern}/: {(shown.Length <= 160 ? shown : shown[..157] + "...")}");
                    break;
                }
            }
        }

        return found;
    }

    internal static List<(string File, string Line)> AddedLines(string patch)
    {
        var lines = new List<(string, string)>();
        var file = "";
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                file = line[4..].Trim();
                file = file.StartsWith("b/", StringComparison.Ordinal) ? file[2..] : file;
            }
            else if (line.StartsWith('+'))
            {
                lines.Add((file, line[1..]));
            }
        }

        return lines;
    }
}
