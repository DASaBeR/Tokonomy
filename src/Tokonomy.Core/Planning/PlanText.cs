using System.Text;
using Tokonomy.Core.Model;

namespace Tokonomy.Core.Planning;

/// <summary>
/// Renders plan content as Markdown for prompts and briefs, so the worker and the architect read a unit the same way.
/// Empty sections are left out.
/// </summary>
public static class PlanText
{
    public static void Architecture(StringBuilder sb, Contract contract)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.Architecture is not { } a)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(a.Summary))
        {
            sb.AppendLine(a.Summary.Trim()).AppendLine();
        }

        if (a.Components.Count > 0)
        {
            sb.AppendLine("Components:");
            foreach (var c in a.Components)
            {
                sb.AppendLine($"- {c.Name} ({c.Change} {c.Path}): {c.Responsibility}");
            }
        }

        List(sb, "Invariants (must hold after every unit)", a.Invariants);
        Line(sb, "Error handling", a.ErrorHandling);
        Line(sb, "Security", a.Security);
        Line(sb, "Performance", a.Performance);
    }

    /// <summary>The decisions with the given ids, or every decision when <paramref name="ids"/> is null.</summary>
    public static void Decisions(StringBuilder sb, Contract contract, IReadOnlyCollection<string>? ids = null)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(contract);
        foreach (var d in contract.Decisions.Where(d => ids is null || ids.Contains(d.Id)))
        {
            sb.AppendLine($"- {d.Id} {d.Title}: {d.Decision}");
            if (!string.IsNullOrWhiteSpace(d.Rationale))
            {
                sb.AppendLine($"  Why: {d.Rationale}");
            }

            if (d.AlternativesRejected.Count > 0)
            {
                sb.AppendLine($"  Rejected: {string.Join("; ", d.AlternativesRejected)}");
            }

            foreach (var c in d.Constraints)
            {
                sb.AppendLine($"  Constraint: {c}");
            }
        }
    }

    /// <summary>The unit's instructions: objective, spec, files, steps, contracts, constraints, edge cases, acceptance, verification.</summary>
    public static void Unit(StringBuilder sb, WorkUnit unit, bool includeAcceptance = true)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(unit);
        Line(sb, "Objective", unit.Objective);
        sb.AppendLine(unit.Spec.Trim());
        if (unit.Create.Count > 0 || unit.Modify.Count > 0)
        {
            sb.AppendLine();
            Line(sb, "Files to create", unit.Create.Count > 0 ? string.Join(", ", unit.Create) : null);
            Line(sb, "Files to modify", unit.Modify.Count > 0 ? string.Join(", ", unit.Modify) : null);
        }
        else if (unit.Files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Files expected to change: " + string.Join(", ", unit.Files));
        }

        Line(sb, "Do not touch", unit.Avoid.Count > 0 ? string.Join(", ", unit.Avoid) : null);
        List(sb, "Existing code to read and reuse", unit.References);
        Numbered(sb, "Implementation steps (in this order)", unit.Steps);
        List(sb, "Interfaces and contracts (exactly as written)", unit.Interfaces);
        List(sb, "Constraints", unit.Constraints);
        List(sb, "Edge cases to handle", unit.EdgeCases);
        if (includeAcceptance)
        {
            List(sb, "Acceptance", unit.Acceptance);
        }

        List(sb, "Verify your work", unit.Verification);
        List(sb, "Expected side effects", unit.SideEffects);
    }

    public static void List(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(sb);
        if (items.Count == 0)
        {
            return;
        }

        sb.AppendLine().AppendLine($"### {title}");
        foreach (var item in items)
        {
            sb.AppendLine($"- {item}");
        }
    }

    private static void Numbered(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        sb.AppendLine().AppendLine($"### {title}");
        for (var i = 0; i < items.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {items[i]}");
        }
    }

    private static void Line(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.AppendLine($"{label}: {value.Trim()}");
        }
    }
}
