using System.Text;
using Taskrail.Core.Model;

namespace Taskrail.Runtime.Executors;

/// <summary>Renders the worker brief: contract excerpt, the unit, rules, the don't list and prior gate feedback.</summary>
public static class BriefWriter
{
    public const int MaxFeedbackChars = 12_000;

    public static Brief Build(Contract contract, WorkUnit unit, int attempt, string briefFileName, string? feedback, IReadOnlyList<WorkUnit> completed)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(completed);

        var sb = new StringBuilder();
        sb.AppendLine("# Taskrail work unit");
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

        return sb.ToString();
    }
}
