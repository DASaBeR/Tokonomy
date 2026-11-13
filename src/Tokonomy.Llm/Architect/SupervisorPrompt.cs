using System.Text;
using System.Text.Json;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Supervision;
using Tokonomy.Core.Takeover;

namespace Tokonomy.Llm.Architect;

/// <summary>Everything the architect sees when it is called to supervise a unit.</summary>
public sealed record SupervisionInput
{
    public required Contract Plan { get; init; }
    public required int Revision { get; init; }
    public required WorkUnit Unit { get; init; }
    public required SupervisionRequest Request { get; init; }
    public required int Attempts { get; init; }
    public WorkerReport? Report { get; init; }
    public string Diff { get; init; } = "";
    public string FailureOutput { get; init; } = "";
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> RepoFiles { get; init; } = [];
    public IReadOnlyList<LockedRegion> Locked { get; init; } = [];
    public IReadOnlyList<Lesson> Lessons { get; init; } = [];
    public IReadOnlyList<string> Deviations { get; init; } = [];

    /// <summary>One line per earlier intervention on this unit (level, diagnosis, outcome).</summary>
    public IReadOnlyList<string> PreviousInterventions { get; init; } = [];

    /// <summary>Review findings when the review triggered this call.</summary>
    public IReadOnlyList<ReviewFinding> ReviewFindings { get; init; } = [];

    /// <summary>Units still to do after this one, so plan updates can reach them.</summary>
    public IReadOnlyList<WorkUnit> Remaining { get; init; } = [];

    /// <summary>Completed units (immutable).</summary>
    public IReadOnlyList<WorkUnit> Completed { get; init; } = [];
}

/// <summary>The architect's decision. Validated by <see cref="SupervisorPrompt.Parse"/> and enforced by the orchestrator.</summary>
public sealed record SupervisorReply
{
    public required InterventionLevel Level { get; init; }
    public string Diagnosis { get; init; } = "";

    /// <summary>worker_mistake, plan_error, missing_context, environment or unclear.</summary>
    public string RootCause { get; init; } = "unclear";

    /// <summary>Corrected instructions for the worker (L1, and guidance after L2).</summary>
    public string? Instructions { get; init; }

    /// <summary>L1 only: throw the worker's attempt away and let it start from the unit start.</summary>
    public bool DiscardWorkerChanges { get; init; }

    public IReadOnlyList<PlanOperation> PlanUpdates { get; init; } = [];
    public IReadOnlyList<PlanOperation> Lessons { get; init; } = [];

    /// <summary>Set when the architect says a human must decide; the unit then stops with this explanation.</summary>
    public string? HumanDecision { get; init; }

    /// <summary>Search/replace edits for L2 and L3.</summary>
    public IReadOnlyList<EditBlock> Edits { get; init; } = [];

    public string? Summary { get; init; }
    public string? Why { get; init; }

    /// <summary>Why the reply cannot be used as is (empty when it can).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool Usable => Problems.Count == 0;
}

/// <summary>
/// Prompt and parser for supervision: the architect diagnoses what went wrong (the worker, or its own plan), picks an
/// intervention level among those policy allows, corrects the remaining plan and records lessons. It answers with a
/// fenced JSON decision, followed by search/replace edit blocks for L2 and L3.
/// </summary>
public static class SupervisorPrompt
{
    public const int MaxDiffChars = 30_000;
    public const int MaxFileChars = 24_000;
    public const int MaxFeedbackChars = 4_000;
    public const int MaxListedFiles = 150;

    public static readonly string System = $$"""
        You are the principal engineer supervising a cheaper model (the worker) that implements your plan unit by unit.
        You were called because something went wrong or needs a decision. Diagnose before you act: is it the worker's
        mistake, an error or gap in YOUR plan (a wrong assumption, a missing decision, an interface that does not exist),
        missing context, or the environment? Do not assume your plan is right; correct it when the evidence says so.

        Choose the smallest intervention that fixes the root cause, among the levels you are allowed:
        - 0 no action: nothing needs to change; the normal process continues.
        - 1 instruction correction: the worker misunderstood or lacked information. Write corrected instructions and
          update the plan; the worker tries again with them. Use "discard_worker_changes": true only when its attempt
          is unsalvageable.
        - 2 guided correction: you apply a small, targeted fix as edit blocks; the checks re-run and the worker continues
          from the corrected state if more is needed.
        - 3 takeover: you finish the unit yourself as edit blocks; the checks re-run and your edit is locked.
        If only a human can decide (a product decision, a dependency or a protected file), set "human_decision".

        Update the remaining plan so the same mistake cannot happen again: fix wrong assumptions in pending units,
        add the missing decisions, constraints, edge cases and acceptance criteria, and record lessons. Completed units
        cannot change. Never weaken acceptance tests, never touch .tokonomy/, never edit locked regions.

        {{PlanJson.OperationsHelp}}

        Answer with a JSON decision in a ```json fence:
        ```json
        {"level": 0|1|2|3, "root_cause": "worker_mistake|plan_error|missing_context|environment|unclear",
         "diagnosis": "what happened and why, citing evidence", "instructions": "what the worker must do now (L1/L2)",
         "discard_worker_changes": false, "plan_updates": [], "lessons": [], "human_decision": null,
         "summary": "what you changed (L2/L3)", "why": "what went wrong (L2/L3)"}
        ```
        For level 2 or 3, after the fence add edit blocks exactly in this form (SEARCH must match the file exactly and
        occur once; an empty SEARCH creates a file):

        <<<<<<< SEARCH path/relative/to/repo.py
        exact lines currently in the file
        =======
        the replacement lines
        >>>>>>> REPLACE
        """;

    public static string Build(SupervisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var plan = input.Plan;
        var unit = input.Unit;
        var sb = new StringBuilder();
        sb.AppendLine("# Overall task").AppendLine(plan.Task.Trim()).AppendLine();
        if (plan.Architecture is not null)
        {
            sb.AppendLine("# Architecture");
            PlanText.Architecture(sb, plan);
            sb.AppendLine();
        }

        if (plan.Decisions.Count > 0)
        {
            sb.AppendLine("# Decisions");
            PlanText.Decisions(sb, plan);
            sb.AppendLine();
        }

        List(sb, "Project rules", plan.Rules.Select(r => $"{r.Id}: {r.Text}"));
        List(sb, "Do not", plan.DontList);
        List(sb, "Lessons so far", input.Lessons.Select(l => $"{l.Id} ({(l.Scope == LessonScope.Global ? "global" : string.Join(",", l.AppliesTo))}): {l.Text}"));

        sb.AppendLine($"# Current unit {unit.Id}: {unit.Title} (plan revision {input.Revision}, risk {unit.Risk.ToString().ToLowerInvariant()})");
        PlanText.Unit(sb, unit);
        if (unit.Corrections.Count > 0)
        {
            PlanText.List(sb, "Corrections already given to the worker", unit.Corrections);
        }

        sb.AppendLine();
        sb.AppendLine($"# Why you were called: {input.Request.Trigger} - {input.Request.Reason}");
        sb.AppendLine($"Allowed levels: {string.Join(", ", input.Request.AllowedLevels.Select(l => (int)l))}. Worker attempts so far: {input.Attempts}.");
        sb.AppendLine();
        if (input.Report is { } report)
        {
            sb.AppendLine("# The worker's report (unverified; judge it against the evidence)");
            sb.AppendLine($"Status: {report.Status}{(report.Reason is null ? "" : $" ({report.Reason})")}; decision required: {report.DecisionRequired}");
            sb.AppendLine(report.Description);
            Bullets(sb, "Evidence", report.Evidence);
            Bullets(sb, "Tried", report.AttemptedActions);
            Bullets(sb, "Discoveries", report.Discoveries);
            Bullets(sb, "Report problems found by Tokonomy", report.Problems);
            sb.AppendLine();
        }

        List(sb, "Plan deviations detected by Tokonomy", input.Deviations);
        List(sb, "Review findings", input.ReviewFindings.Select(f => f.ToString()));
        List(sb, "Earlier interventions on this unit", input.PreviousInterventions);

        sb.AppendLine("# The worker's changes (diff against the start of this unit)");
        sb.AppendLine(input.Diff.Length == 0 ? "(no changes)" : "```diff\n" + Head(input.Diff, MaxDiffChars).TrimEnd() + "\n```").AppendLine();
        if (!string.IsNullOrWhiteSpace(input.FailureOutput))
        {
            sb.AppendLine("# Failure output from the checks");
            sb.AppendLine("```").AppendLine(Tail(input.FailureOutput, MaxFeedbackChars)).AppendLine("```").AppendLine();
        }

        if (input.Locked.Count > 0)
        {
            sb.AppendLine("# Locked regions (never modify these lines)");
            foreach (var region in input.Locked)
            {
                sb.AppendLine($"### {region.Path}").AppendLine("```").AppendLine(region.Text.Trim('\n')).AppendLine("```");
            }

            sb.AppendLine();
        }

        sb.AppendLine("# Current content of the relevant files");
        if (input.Files.Count == 0)
        {
            sb.AppendLine("(none)");
        }

        foreach (var (path, content) in input.Files)
        {
            sb.AppendLine($"### {path}").AppendLine("```").AppendLine(Head(content, MaxFileChars).TrimEnd('\n')).AppendLine("```").AppendLine();
        }

        List(sb, "Remaining units (pending; you may update them)", input.Remaining.Select(u => $"{u.Id}: {u.Title}{(u.Objective is null ? "" : " - " + u.Objective)}{(u.DependsOn.Count > 0 ? $" (after {string.Join(", ", u.DependsOn)})" : "")}"));
        List(sb, "Completed units (immutable)", input.Completed.Select(u => $"{u.Id}: {u.Title}"));
        if (input.RepoFiles.Count > 0)
        {
            sb.AppendLine("# Files in the repository");
            sb.AppendLine(string.Join('\n', input.RepoFiles.Take(MaxListedFiles))).AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses and validates the reply against what policy allows: a level outside <paramref name="allowed"/>, an L1
    /// without instructions or an L2/L3 without edits is reported as a problem and never acted on.
    /// </summary>
    public static SupervisorReply Parse(string text, IReadOnlyList<InterventionLevel> allowed, string unitId)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(allowed);
        var (json, rest) = PlanJson.SplitFenced(text);
        var dto = PlanJson.Deserialize<ReplyDto>(json);
        if (dto is null)
        {
            return new SupervisorReply { Level = InterventionLevel.None, Problems = ["the reply had no readable JSON decision"] };
        }

        var problems = new List<string>();
        var level = ParseLevel(dto.Level);
        if (level is null)
        {
            problems.Add($"unknown level '{dto.Level}'");
            level = InterventionLevel.None;
        }

        var edits = EditBlocks.Parse(rest);
        var human = string.IsNullOrWhiteSpace(dto.HumanDecision) ? null : dto.HumanDecision.Trim();
        if (human is null)
        {
            if (!allowed.Contains(level.Value))
            {
                problems.Add($"level {(int)level.Value} is not allowed here (allowed: {string.Join(", ", allowed.Select(l => (int)l))})");
            }

            if (level == InterventionLevel.InstructionCorrection && string.IsNullOrWhiteSpace(dto.Instructions))
            {
                problems.Add("an instruction correction needs instructions");
            }

            if (level is InterventionLevel.GuidedCorrection or InterventionLevel.Takeover && edits.Count == 0)
            {
                problems.Add($"level {(int)level.Value} needs edit blocks after the JSON");
            }
        }

        return new SupervisorReply
        {
            Level = level.Value,
            Diagnosis = dto.Diagnosis?.Trim() ?? "",
            RootCause = NormalizeCause(dto.RootCause),
            Instructions = string.IsNullOrWhiteSpace(dto.Instructions) ? null : dto.Instructions.Trim(),
            DiscardWorkerChanges = dto.DiscardWorkerChanges == true,
            PlanUpdates = (dto.PlanUpdates ?? []).Select(o => o.ToModel()).ToList(),
            Lessons = (dto.Lessons ?? []).Select(l => l.ToOperation(unitId)).ToList(),
            HumanDecision = human,
            Edits = level is InterventionLevel.GuidedCorrection or InterventionLevel.Takeover ? edits : [],
            Summary = string.IsNullOrWhiteSpace(dto.Summary) ? null : dto.Summary.Trim(),
            Why = string.IsNullOrWhiteSpace(dto.Why) ? null : dto.Why.Trim(),
            Problems = problems,
        };
    }

    internal static InterventionLevel? ParseLevel(JsonElement? value)
    {
        switch (value)
        {
            case { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var i) && i is >= 0 and <= 3:
                return (InterventionLevel)i;
            case { ValueKind: JsonValueKind.String } s:
                var text = s.GetString()!.Trim().ToLowerInvariant().Replace("level", "", StringComparison.Ordinal).Replace(' ', '_').Trim('_');
                return text switch
                {
                    "0" or "none" or "no_action" => InterventionLevel.None,
                    "1" or "l1" or "instruction_correction" => InterventionLevel.InstructionCorrection,
                    "2" or "l2" or "guided_correction" => InterventionLevel.GuidedCorrection,
                    "3" or "l3" or "takeover" or "architect_takeover" => InterventionLevel.Takeover,
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static string NormalizeCause(string? cause) => cause?.Trim().ToLowerInvariant().Replace(' ', '_') switch
    {
        "worker_mistake" or "worker_error" or "implementation_bug" => "worker_mistake",
        "plan_error" or "plan_mistake" or "architect_error" or "wrong_assumption" => "plan_error",
        "missing_context" => "missing_context",
        "environment" => "environment",
        _ => "unclear",
    };

    private static void List(StringBuilder sb, string title, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        sb.AppendLine($"# {title}");
        foreach (var item in list)
        {
            sb.AppendLine($"- {item}");
        }

        sb.AppendLine();
    }

    private static void Bullets(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        if (items.Count > 0)
        {
            sb.AppendLine($"{title}:");
            foreach (var item in items)
            {
                sb.AppendLine($"- {item}");
            }
        }
    }

    private static string Head(string text, int max) => text.Length <= max ? text : text[..max] + "\n... (truncated)";

    private static string Tail(string text, int max) => text.Length <= max ? text.Trim() : "...\n" + text[^max..].Trim();

    private sealed class ReplyDto
    {
        public JsonElement? Level { get; set; }
        public string? Diagnosis { get; set; }
        public string? RootCause { get; set; }
        public string? Instructions { get; set; }
        public bool? DiscardWorkerChanges { get; set; }
        public List<PlanJson.OperationDto>? PlanUpdates { get; set; }
        public List<PlanJson.LessonDto>? Lessons { get; set; }
        public string? HumanDecision { get; set; }
        public string? Summary { get; set; }
        public string? Why { get; set; }
    }
}
