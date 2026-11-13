using System.Text;
using Tokonomy.Core.Planning;

namespace Tokonomy.Llm.Architect;

public enum CritiqueVerdict
{
    Ready,
    Revise,

    /// <summary>The critique could not be obtained or read; it never blocks a plan on its own.</summary>
    Unavailable,
}

public sealed record CritiqueFinding(string Severity, string Category, string? Unit, string Issue, string? Fix)
{
    public bool Blocking => Severity is "high" or "critical";

    public override string ToString() => $"[{Severity}/{Category}] {(Unit is null ? "" : Unit + ": ")}{Issue}{(Fix is null ? "" : " Fix: " + Fix)}";
}

public sealed record PlanCritique(CritiqueVerdict Verdict, string Summary, IReadOnlyList<CritiqueFinding> Findings)
{
    public bool Accepts => Verdict != CritiqueVerdict.Revise || !Findings.Any(f => f.Severity is "high" or "critical" or "medium");

    /// <summary>The critique's findings as plan-quality issues: high severity blocks, the rest is advice.</summary>
    public IEnumerable<PlanIssue> AsIssues() => Findings.Select(f => new PlanIssue(
        f.Category is "architecture" or "consistency" or "contradiction" ? PlanReadiness.ArchitecturallyInconsistent : PlanReadiness.Incomplete,
        "critique_" + f.Category, f.Unit, f.Issue + (f.Fix is null ? "" : $" (fix: {f.Fix})"), f.Blocking));
}

/// <summary>
/// A separate architect call that reviews a plan as a skeptical principal engineer would, before anyone sees it. It
/// does not rewrite the plan; its findings go back to the planner for one revision.
/// </summary>
public static class PlanCritic
{
    public const int MaxContextChars = 24_000;

    public const string System = """
        You are a skeptical principal engineer reviewing an implementation plan before a cheaper model executes it unit by
        unit. The cheaper model follows instructions literally and must not make design decisions, so any gap in the plan
        becomes a mistake in the code. Review the plan; do not rewrite it.

        Check: every requirement of the task is covered; important decisions are explicit (not left to the worker);
        dependencies and ordering are correct; units are small and single-purpose; steps are concrete and actionable;
        files to modify really exist in the repository context and nothing is invented; acceptance criteria are
        measurable; tests are sufficient; edge cases are not missing; nothing contradicts the architecture or the
        decisions; there are no unnecessary changes or hidden assumptions; no instruction could reasonably be
        misunderstood.

        Severity: high = the plan will likely produce wrong or failing work if executed as is; medium = a real gap the
        worker may fill wrongly; low = minor. Report only real problems; an empty findings list is a valid answer.

        Respond with ONLY a JSON object:
        {"verdict": "ready" | "revise", "summary": "one sentence",
         "findings": [{"severity": "high|medium|low", "category": "coverage|decision|dependency|size|actionability|grounding|acceptance|tests|edge_case|architecture|consistency|scope|ambiguity", "unit": "U1 or null", "issue": "...", "fix": "..."}]}
        """;

    public static string Build(string task, string repoContext, string planJson, IEnumerable<PlanIssue> knownIssues)
    {
        ArgumentNullException.ThrowIfNull(task);
        var sb = new StringBuilder();
        sb.AppendLine("# Task").AppendLine(task.Trim()).AppendLine();
        sb.AppendLine("# Repository context (collected by Tokonomy)");
        sb.AppendLine(repoContext.Length <= MaxContextChars ? repoContext : repoContext[..MaxContextChars] + "\n... (context truncated)").AppendLine();
        var known = knownIssues.ToList();
        if (known.Count > 0)
        {
            sb.AppendLine("# Issues the deterministic checks already found (do not repeat them)");
            foreach (var issue in known)
            {
                sb.AppendLine($"- {issue}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("# Plan to review").AppendLine("```json").AppendLine(planJson).AppendLine("```");
        return sb.ToString();
    }

    public static PlanCritique Parse(string text)
    {
        var dto = PlanJson.Deserialize<CritiqueDto>(PlanJson.ExtractJson(text ?? ""));
        if (dto?.Verdict is null)
        {
            return new PlanCritique(CritiqueVerdict.Unavailable, "the critique reply could not be read", []);
        }

        var verdict = dto.Verdict.Trim().ToLowerInvariant() switch
        {
            "ready" or "approve" or "ok" => CritiqueVerdict.Ready,
            "revise" or "request_changes" or "reject" => CritiqueVerdict.Revise,
            _ => CritiqueVerdict.Unavailable,
        };
        var findings = (dto.Findings ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Issue))
            .Select(f => new CritiqueFinding(
                Severity(f.Severity), string.IsNullOrWhiteSpace(f.Category) ? "general" : f.Category.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(f.Unit) || f.Unit == "null" ? null : f.Unit.Trim(), f.Issue!.Trim(), string.IsNullOrWhiteSpace(f.Fix) ? null : f.Fix.Trim()))
            .Take(20)
            .ToList();
        return new PlanCritique(verdict, dto.Summary?.Trim() ?? "", findings);
    }

    private static string Severity(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "critical" or "high" => "high",
        "medium" or "moderate" => "medium",
        _ => "low",
    };

    private sealed class CritiqueDto
    {
        public string? Verdict { get; set; }
        public string? Summary { get; set; }
        public List<FindingDto>? Findings { get; set; }
    }

    private sealed class FindingDto
    {
        public string? Severity { get; set; }
        public string? Category { get; set; }
        public string? Unit { get; set; }
        public string? Issue { get; set; }
        public string? Fix { get; set; }
    }
}
