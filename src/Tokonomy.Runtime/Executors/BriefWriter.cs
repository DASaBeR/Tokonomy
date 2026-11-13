using System.Text;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Supervision;
using Tokonomy.Core.Takeover;

namespace Tokonomy.Runtime.Executors;

/// <summary>Everything the worker brief is compiled from. The plan must be the current revision, never a stale copy.</summary>
public sealed record BriefInput
{
    public required Contract Plan { get; init; }
    public required WorkUnit Unit { get; init; }
    public required int Attempt { get; init; }
    public required string BriefFileName { get; init; }
    public string? Feedback { get; init; }
    public IReadOnlyList<WorkUnit> Completed { get; init; } = [];
    public IReadOnlyList<Handoff> Handoffs { get; init; } = [];
    public (IReadOnlyList<string> Files, string Command)? Acceptance { get; init; }

    /// <summary>The plan revision the brief was compiled from; shown when the plan changed during the run.</summary>
    public int Revision { get; init; } = 1;

    /// <summary>Lessons that apply to this unit.</summary>
    public IReadOnlyList<Lesson> Lessons { get; init; } = [];

    /// <summary>Ask the worker to write <see cref="WorkerReports.FileName"/> (adaptive supervision reads it).</summary>
    public bool RequestReport { get; init; }
}

/// <summary>
/// Compiles the worker brief: an operational instruction package written into the agent's native instructions file.
/// It carries the objective, the architecture and decisions that bind the unit, the exact steps, the files, the
/// constraints and don'ts, acceptance and verification, completed work, the architect's corrections and lessons, the
/// plan revision, the expected report format and, on a retry, the failure to fix.
/// </summary>
public static class BriefWriter
{
    public const int MaxFeedbackChars = 12_000;
    public const int MaxDecisionsWhenUnscoped = 6;

    /// <summary>The original entry point; kept for callers that have no revision, lessons or report.</summary>
    public static Brief Build(
        Contract contract, WorkUnit unit, int attempt, string briefFileName, string? feedback, IReadOnlyList<WorkUnit> completed,
        IReadOnlyList<Handoff>? handoffs = null, (IReadOnlyList<string> Files, string Command)? acceptance = null) =>
        Compile(new BriefInput
        {
            Plan = contract ?? throw new ArgumentNullException(nameof(contract)),
            Unit = unit ?? throw new ArgumentNullException(nameof(unit)),
            Attempt = attempt,
            BriefFileName = briefFileName,
            Feedback = feedback,
            Completed = completed ?? throw new ArgumentNullException(nameof(completed)),
            Handoffs = handoffs ?? [],
            Acceptance = acceptance,
        });

    public static Brief Compile(BriefInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var contract = input.Plan;
        var unit = input.Unit;
        var sb = new StringBuilder();
        sb.AppendLine("# Tokonomy work unit");
        sb.AppendLine();
        sb.AppendLine("You are implementing one unit of a larger task. Make the smallest change that satisfies this unit.");
        sb.AppendLine("Do not commit, do not create branches, and do not edit files outside this repository.");
        sb.AppendLine("Your changes are verified by automated gates (build, lint, tests); your own report is not used.");
        if (contract.IsBlueprint || input.RequestReport)
        {
            sb.AppendLine();
            sb.AppendLine("## How to work");
            sb.AppendLine("The architect has already made the design decisions. Follow the steps below in order and implement exactly what they specify.");
            sb.AppendLine("You MAY: choose local names, follow the conventions of the surrounding code, read any file in the repository, make small implementation-level choices.");
            sb.AppendLine("You MUST NOT: redesign the architecture, change established interfaces or contracts, add dependencies, modify files outside this unit, change the requirements, or silently switch to a different approach.");
            sb.AppendLine(input.RequestReport
                ? "If something the plan assumes is missing or wrong, or a decision is needed that the plan does not make, stop and report it (see \"Report\") instead of guessing."
                : "If something the plan assumes is missing or wrong, do the closest thing the plan allows and keep the change minimal.");
        }

        if (input.Revision > 1)
        {
            sb.AppendLine();
            sb.AppendLine($"Plan revision {input.Revision}: the plan was updated during this run. These instructions supersede anything you saw before.");
        }

        sb.AppendLine();
        sb.AppendLine("## Overall task");
        sb.AppendLine(contract.Task.Trim());

        if (contract.Architecture is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Architecture (decided; follow it)");
            PlanText.Architecture(sb, contract);
        }

        var decisions = unit.Decisions.Count > 0 ? unit.Decisions : contract.Decisions.Count <= MaxDecisionsWhenUnscoped ? null : [];
        if (contract.Decisions.Count > 0 && decisions is not { Count: 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Decisions that bind this unit");
            PlanText.Decisions(sb, contract, decisions);
        }

        sb.AppendLine();
        sb.AppendLine($"## Your unit: {unit.Id} - {unit.Title}");
        PlanText.Unit(sb, unit);

        if (input.Acceptance is { } tests)
        {
            sb.AppendLine();
            sb.AppendLine("## Acceptance tests (written by the architect; read-only)");
            sb.AppendLine($"Your work is accepted only when `{tests.Command}` passes. These files define done:");
            foreach (var file in tests.Files)
            {
                sb.AppendLine($"- {file}");
            }

            sb.AppendLine("Read them first and build exactly what they check. Do not edit, delete or add files in their directory: any change is reverted before they run.");
        }

        if (contract.Rules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Rules (must follow)");
            foreach (var rule in contract.Rules)
            {
                sb.AppendLine($"- {rule.Id}: {rule.Text}");
            }
        }

        if (contract.DontList.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Do not");
            foreach (var d in contract.DontList)
            {
                sb.AppendLine($"- {d}");
            }
        }

        if (input.Lessons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Lessons from earlier work in this run (mistakes not to repeat)");
            foreach (var lesson in input.Lessons)
            {
                var enforced = lesson.ForbiddenPattern is null ? "" : " (enforced by an automated check)";
                sb.AppendLine($"- {lesson.Id}: {lesson.Text}{enforced}");
            }
        }

        if (unit.Corrections.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Corrections from the architect (follow these; they override the steps above where they differ)");
            foreach (var correction in unit.Corrections)
            {
                sb.AppendLine($"- {correction}");
            }
        }

        if (input.Completed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Already done (committed; build on it, do not redo it)");
            foreach (var u in input.Completed)
            {
                sb.AppendLine($"- {u.Id}: {u.Title}");
            }
        }

        if (input.Handoffs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Handoff from the architect");
            sb.AppendLine("An earlier unit needed the architect to step in. Build on its fix and follow these notes:");
            foreach (var handoff in input.Handoffs)
            {
                sb.AppendLine();
                sb.AppendLine($"- {handoff.UnitId} ({handoff.UnitTitle}): {handoff.Summary}");
                sb.AppendLine($"  What went wrong: {handoff.Why}");
                foreach (var region in handoff.Locked)
                {
                    var text = region.Text.Trim('\n');
                    sb.AppendLine($"  Locked in {region.Path} (an automated check fails if these lines change):");
                    sb.AppendLine("  ```");
                    foreach (var line in (text.Length <= 1200 ? text : text[..1200] + "\n...").Split('\n'))
                    {
                        sb.AppendLine("  " + line);
                    }

                    sb.AppendLine("  ```");
                }
            }
        }

        if (input.RequestReport)
        {
            sb.AppendLine();
            sb.AppendLine("## Report");
            sb.AppendLine($"When you finish (or stop), write `{WorkerReports.FileName}` in the repository root. Tokonomy reads it and removes it; it is never part of your change.");
            sb.AppendLine("The automated gates decide whether the unit is accepted; the report is how you tell the architect about problems. Use:");
            sb.AppendLine("```json");
            sb.AppendLine("""{"status": "completed | blocked | clarification_required | unexpected_discovery | failed", "reason": "missing_dependency | missing_interface | plan_conflict | ambiguous_requirement | other", "description": "what happened", "evidence": ["paths that show it"], "attempted_actions": ["what you tried"], "discoveries": ["facts the plan did not know"], "decision_required": false}""");
            sb.AppendLine("```");
            sb.AppendLine("Report \"blocked\" or set decision_required when a file, interface or dependency the plan relies on does not exist, when the plan contradicts the code, or when you would otherwise have to make a design decision.");
        }

        if (!string.IsNullOrWhiteSpace(input.Feedback))
        {
            var feedback = input.Feedback;
            sb.AppendLine();
            sb.AppendLine($"## Previous attempt failed (this is attempt {input.Attempt})");
            sb.AppendLine("Your earlier changes are still in the working tree. Fix the problems below; do not start over unless necessary.");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(feedback.Length <= MaxFeedbackChars ? feedback.Trim() : "...\n" + feedback[^MaxFeedbackChars..].Trim());
            sb.AppendLine("```");
        }

        return new Brief
        {
            Body = sb.ToString(),
            Prompt = $"Read {input.BriefFileName} in the repository root and complete the work unit it describes. Do not commit.",
            Label = $"{unit.Id}-a{input.Attempt}",
        };
    }

    /// <summary>Formats blocking gate results as worker feedback.</summary>
    public static string Feedback(string decisionReason, IEnumerable<GateResult> gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        var sb = new StringBuilder();
        sb.AppendLine(decisionReason);
        foreach (var gate in gates.Where(g => g.Blocking))
        {
            sb.AppendLine();
            sb.AppendLine($"--- gate: {gate.Gate} ---");
            if (gate.NewFailures.Count > 0)
            {
                sb.AppendLine("New failing tests: " + string.Join(", ", gate.NewFailures.Take(20)));
            }

            sb.AppendLine(gate.Output);
        }

        // Gate output comes from code the worker may have written and goes straight back to the model: a test that
        // prints a credential file must not deliver it. (Shape-based, so it is a mitigation, not a guarantee.)
        return Tokonomy.Runtime.Ledger.Redactor.Redact(sb.ToString());
    }
}
