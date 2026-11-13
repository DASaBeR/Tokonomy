using System.Text;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Supervision;

namespace Tokonomy.Llm.Architect;

public sealed record UnitReviewInput
{
    public required Contract Plan { get; init; }
    public required int Revision { get; init; }
    public required WorkUnit Unit { get; init; }
    public required string Diff { get; init; }
    public required string Why { get; init; }
    public IReadOnlyList<string> Deviations { get; init; } = [];
    public IReadOnlyList<string> Gates { get; init; } = [];
    public WorkerReport? Report { get; init; }
    public IReadOnlyList<Lesson> Lessons { get; init; } = [];
}

/// <summary>The review plus the plan updates and lessons the reviewer proposed (applied only after validation).</summary>
public sealed record UnitReviewReply(UnitReview Review, IReadOnlyList<PlanOperation> PlanUpdates, IReadOnlyList<PlanOperation> Lessons);

/// <summary>
/// Architectural review of one unit whose gates passed. The architect checks what gates cannot: the plan was followed,
/// responsibilities and abstractions are right, nothing unrelated changed, no needless dependency or duplicated logic.
/// </summary>
public static class UnitReviewPrompt
{
    public const int MaxDiffChars = 40_000;

    public static readonly string System = $$"""
        You are the principal engineer reviewing one unit of work a cheaper model implemented from your plan. Every
        automated check (build, lint, tests) already passed; do not repeat them. Review only what checks miss:
        architectural compliance and the decisions in the plan, correct responsibilities and abstraction boundaries,
        consistency with existing patterns, unnecessary dependencies, duplicated business logic, missing edge cases,
        unintended API changes, performance problems, unrelated modifications and deviations from the unit's plan.
        Do not raise style nits. Prefer approving with warnings over requesting changes for minor issues.

        Verdicts: approve; approve_with_warnings (minor issues, noted); request_changes (a real problem the worker must
        fix now, with concrete recommendations); blocked (the unit cannot be accepted and the worker cannot fix it);
        human_decision_required (only a human can decide).

        You may also update the remaining plan and record lessons, using the same operations as when supervising:
        {{PlanJson.OperationsHelp}}

        Respond with ONLY a JSON object:
        {"verdict": "approve|approve_with_warnings|request_changes|blocked|human_decision_required", "summary": "one sentence",
         "findings": [{"severity": "high|medium|low", "category": "architectural_violation|plan_deviation|unnecessary_dependency|duplication|edge_case|api_change|performance|unrelated_change|other",
           "file": "path", "description": "...", "recommendation": "..."}],
         "plan_updates": [], "lessons": []}
        """;

    public static string Build(UnitReviewInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new StringBuilder();
        sb.AppendLine("# Overall task").AppendLine(input.Plan.Task.Trim()).AppendLine();
        if (input.Plan.Architecture is not null)
        {
            sb.AppendLine("# Architecture");
            PlanText.Architecture(sb, input.Plan);
            sb.AppendLine();
        }

        if (input.Plan.Decisions.Count > 0)
        {
            sb.AppendLine("# Decisions");
            PlanText.Decisions(sb, input.Plan);
            sb.AppendLine();
        }

        if (input.Plan.Rules.Count > 0 || input.Plan.DontList.Count > 0)
        {
            sb.AppendLine("# Rules and don'ts");
            foreach (var rule in input.Plan.Rules)
            {
                sb.AppendLine($"- {rule.Id}: {rule.Text}");
            }

            foreach (var dont in input.Plan.DontList)
            {
                sb.AppendLine($"- Do not: {dont}");
            }

            sb.AppendLine();
        }

        foreach (var lesson in input.Lessons)
        {
            sb.AppendLine($"- Lesson {lesson.Id}: {lesson.Text}");
        }

        sb.AppendLine($"# Unit {input.Unit.Id}: {input.Unit.Title} (plan revision {input.Revision}, risk {input.Unit.Risk.ToString().ToLowerInvariant()})");
        PlanText.Unit(sb, input.Unit);
        sb.AppendLine().AppendLine($"# Why this unit is reviewed: {input.Why}");
        if (input.Gates.Count > 0)
        {
            sb.AppendLine("Checks: " + string.Join(", ", input.Gates));
        }

        foreach (var deviation in input.Deviations)
        {
            sb.AppendLine($"- Deviation found by Tokonomy: {deviation}");
        }

        if (input.Report is { } report)
        {
            sb.AppendLine($"Worker report (unverified): {report.Summary}");
            foreach (var d in report.Discoveries)
            {
                sb.AppendLine($"- Discovery: {d}");
            }
        }

        sb.AppendLine().AppendLine("# Diff of this unit");
        var shown = input.Diff.Length <= MaxDiffChars ? input.Diff.TrimEnd() : input.Diff[..MaxDiffChars] + "\n... (diff truncated)";
        sb.AppendLine(input.Diff.Length == 0 ? "(empty)" : "```diff\n" + shown + "\n```");
        return sb.ToString();
    }

    public static UnitReviewReply Parse(string text, string model, string unitId)
    {
        var dto = PlanJson.Deserialize<ReviewDto>(PlanJson.ExtractJson(text ?? ""));
        var verdict = dto?.Verdict?.Trim().ToLowerInvariant().Replace(' ', '_') switch
        {
            "approve" or "approved" => UnitReviewVerdict.Approve,
            "approve_with_warnings" or "warnings" => UnitReviewVerdict.ApproveWithWarnings,
            "request_changes" or "changes_requested" => UnitReviewVerdict.RequestChanges,
            "blocked" => UnitReviewVerdict.Blocked,
            "human_decision_required" or "human_decision" => UnitReviewVerdict.HumanDecisionRequired,
            _ => (UnitReviewVerdict?)null,
        };
        if (dto is null || verdict is null)
        {
            return new UnitReviewReply(new UnitReview(UnitReviewVerdict.Unavailable, "the review reply could not be read", [], model), [], []);
        }

        var findings = (dto.Findings ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Description))
            .Select(f => new ReviewFinding(
                f.Severity?.Trim().ToLowerInvariant() is { Length: > 0 } s ? s : "low",
                f.Category?.Trim().ToLowerInvariant() is { Length: > 0 } c ? c : "other",
                string.IsNullOrWhiteSpace(f.File) ? null : f.File.Trim(),
                f.Description!.Trim(),
                string.IsNullOrWhiteSpace(f.Recommendation) ? null : f.Recommendation.Trim()))
            .Take(20)
            .ToList();

        // "approve" with high-severity findings contradicts itself: trust the findings and ask for changes.
        if (verdict == UnitReviewVerdict.Approve && findings.Any(f => f.High))
        {
            verdict = UnitReviewVerdict.RequestChanges;
        }
        else if (verdict == UnitReviewVerdict.Approve && findings.Count > 0)
        {
            verdict = UnitReviewVerdict.ApproveWithWarnings;
        }

        return new UnitReviewReply(
            new UnitReview(verdict.Value, dto.Summary?.Trim() ?? "", findings, model),
            (dto.PlanUpdates ?? []).Select(o => o.ToModel()).ToList(),
            (dto.Lessons ?? []).Select(l => l.ToOperation(unitId)).ToList());
    }

    private sealed class ReviewDto
    {
        public string? Verdict { get; set; }
        public string? Summary { get; set; }
        public List<FindingDto>? Findings { get; set; }
        public List<PlanJson.OperationDto>? PlanUpdates { get; set; }
        public List<PlanJson.LessonDto>? Lessons { get; set; }
    }

    private sealed class FindingDto
    {
        public string? Severity { get; set; }
        public string? Category { get; set; }
        public string? File { get; set; }
        public string? Description { get; set; }
        public string? Recommendation { get; set; }
    }
}

/// <summary>Prompt and parser for splitting an oversized unit into smaller units.</summary>
public static class DecomposePrompt
{
    public static readonly string System = """
        You are the principal engineer. A unit of your plan turned out too large for the cheaper worker: its change grew
        past the size limit without passing the checks. Split the unit into 2 to 5 smaller units that together deliver
        exactly what the original unit promised, each with one responsibility and concrete steps. Keep the original's
        decisions and constraints. Order them so each builds on the previous one. Do not include tests; the original
        unit's acceptance tests and criteria move to the last part automatically.

        Respond with ONLY a JSON object:
        {"reason": "why this split", "parts": [{"id": "U3a", "title": "...", "objective": "...", "spec": "...",
          "depends_on": [], "files": {"create": [], "modify": [], "avoid": []}, "references": [], "steps": ["..."],
          "interfaces": [], "constraints": [], "edge_cases": [], "acceptance": ["..."], "verification": [], "risk": "low"}]}
        Part ids must be new (for example the original id followed by a, b, c).
        """;

    public static string Build(Contract plan, WorkUnit unit, string reason, IReadOnlyList<string> changedFiles, int diffLines, string failureOutput)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(unit);
        var sb = new StringBuilder();
        sb.AppendLine("# Overall task").AppendLine(plan.Task.Trim()).AppendLine();
        if (plan.Architecture is not null)
        {
            sb.AppendLine("# Architecture");
            PlanText.Architecture(sb, plan);
            sb.AppendLine();
        }

        sb.AppendLine($"# Unit to split: {unit.Id} - {unit.Title}");
        PlanText.Unit(sb, unit);
        sb.AppendLine().AppendLine($"# What happened: {reason}");
        sb.AppendLine($"The worker's attempt changed {diffLines} lines in: {string.Join(", ", changedFiles.Take(30))}");
        if (!string.IsNullOrWhiteSpace(failureOutput))
        {
            var tail = failureOutput.Length <= 3000 ? failureOutput : "..." + failureOutput[^3000..];
            sb.AppendLine().AppendLine("Check output:").AppendLine("```").AppendLine(tail.Trim()).AppendLine("```");
        }

        return sb.ToString();
    }

    public static (IReadOnlyList<WorkUnit> Parts, string Reason) Parse(string text)
    {
        var dto = PlanJson.Deserialize<SplitDto>(PlanJson.ExtractJson(text ?? ""));
        return dto?.Parts is { Count: > 0 } parts
            ? (parts.Select(p => p.ToUnit()).ToList(), dto.Reason?.Trim() ?? "")
            : ([], "");
    }

    private sealed class SplitDto
    {
        public string? Reason { get; set; }
        public List<PlanJson.UnitDto>? Parts { get; set; }
    }
}
