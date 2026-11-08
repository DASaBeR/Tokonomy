using System.Text.RegularExpressions;
using Taskrail.Core.Model;

namespace Taskrail.Core.Planning;

/// <summary>Validates a contract's shape and dependency DAG, and orders units for sequential execution.</summary>
public static partial class ContractValidator
{
    public const int MaxUnits = 20;

    public static IReadOnlyList<string> Validate(Contract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(contract.Task))
        {
            errors.Add("task is empty");
        }

        if (contract.Units.Count == 0)
        {
            errors.Add("contract has no units");
        }
        else if (contract.Units.Count > MaxUnits)
        {
            errors.Add($"contract has {contract.Units.Count} units; the maximum is {MaxUnits}");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in contract.Units)
        {
            if (string.IsNullOrWhiteSpace(unit.Id) || !IdRegex().IsMatch(unit.Id))
            {
                errors.Add($"unit id '{unit.Id}' must match {IdRegex()}");
            }
            else if (!ids.Add(unit.Id))
            {
                errors.Add($"duplicate unit id '{unit.Id}'");
            }

            if (string.IsNullOrWhiteSpace(unit.Spec))
            {
                errors.Add($"unit '{unit.Id}' has an empty spec");
            }
        }

        foreach (var unit in contract.Units)
        {
            foreach (var dep in unit.DependsOn)
            {
                if (dep == unit.Id)
                {
                    errors.Add($"unit '{unit.Id}' depends on itself");
                }
                else if (!ids.Contains(dep))
                {
                    errors.Add($"unit '{unit.Id}' depends on unknown unit '{dep}'");
                }
            }
        }

        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in contract.Rules)
        {
            if (!ruleIds.Add(rule.Id))
            {
                errors.Add($"duplicate rule id '{rule.Id}'");
            }
        }

        if (errors.Count == 0 && TryOrder(contract, out _) is { } cycle)
        {
            errors.Add($"dependency cycle: {cycle}");
        }

        return errors;
    }

    /// <summary>Topological order, stable with respect to the contract's unit order. Throws on invalid DAGs.</summary>
    public static IReadOnlyList<WorkUnit> Order(Contract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return TryOrder(contract, out var ordered) is { } cycle
            ? throw new InvalidOperationException($"dependency cycle: {cycle}")
            : ordered;
    }

    private static string? TryOrder(Contract contract, out List<WorkUnit> ordered)
    {
        ordered = [];
        var done = new HashSet<string>(StringComparer.Ordinal);
        var remaining = contract.Units.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(u => u.DependsOn.All(done.Contains));
            if (next is null)
            {
                return string.Join(", ", remaining.Select(u => u.Id));
            }

            ordered.Add(next);
            done.Add(next.Id);
            remaining.Remove(next);
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,31}$")]
    private static partial Regex IdRegex();
}
