using System.Text;
using Tokonomy.Core.Model;
using Tokonomy.Core.Takeover;

namespace Tokonomy.Runtime.Executors;

/// <summary>Renders the worker brief: contract excerpt, the unit, rules, the don't list and prior gate feedback.</summary>
public static class BriefWriter
{
    public const int MaxFeedbackChars = 12_000;

    public static Brief Build(
        Contract contract, WorkUnit unit, int attempt, string briefFileName, string? feedback, IReadOnlyList<WorkUnit> completed,
        IReadOnlyList<Handoff>? handoffs = null, (IReadOnlyList<string> Files, string Command)? acceptance = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(completed);

        var sb = new StringBuilder();
        sb.AppendLine("# Tokonomy work unit");
        sb.AppendLine();
        sb.AppendLine("You are implementing one unit of a larger task. Make the smallest change that satisfies this unit.");
        sb.AppendLine("Do not commit, do not create branches, and do not edit files outside this repository.");
        sb.AppendLine("Your changes are verified by automated gates (build, lint, tests); your own report is not used.");
        sb.AppendLine();
        sb.AppendLine("## Overall task");
        sb.AppendLine(contract.Task.Trim());
        sb.AppendLine();
        sb.AppendLine($"## Your unit: {unit.Id} - {unit.Title}");
        sb.AppendLine(unit.Spec.Trim());
        if (unit.Files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Files expected to change: " + string.Join(", ", unit.Files));
        }

        if (unit.Acceptance.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Acceptance");
            foreach (var a in unit.Acceptance)
            {
                sb.AppendLine($"- {a}");
            }
        }

        if (acceptance is { } tests)
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

        if (completed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Already done (committed; build on it, do not redo it)");
            foreach (var u in completed)
            {
                sb.AppendLine($"- {u.Id}: {u.Title}");
            }
        }

        if (handoffs is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Handoff from the architect");
            sb.AppendLine("An earlier unit needed the architect to step in. Build on its fix and follow these notes:");
            foreach (var handoff in handoffs)
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

        if (!string.IsNullOrWhiteSpace(feedback))
        {
            sb.AppendLine();
            sb.AppendLine($"## Previous attempt failed (this is attempt {attempt})");
            sb.AppendLine("Your earlier changes are still in the working tree. Fix the problems below; do not start over unless necessary.");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(feedback.Length <= MaxFeedbackChars ? feedback.Trim() : "...\n" + feedback[^MaxFeedbackChars..].Trim());
            sb.AppendLine("```");
        }

        return new Brief
        {
            Body = sb.ToString(),
            Prompt = $"Read {briefFileName} in the repository root and complete the work unit it describes. Do not commit.",
            Label = $"{unit.Id}-a{attempt}",
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
