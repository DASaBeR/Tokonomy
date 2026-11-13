using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;

namespace Tokonomy.Core.Planning;

/// <summary>
/// How far a plan is from execution-ready. Ordered from worst to best: a plan is only as good as its worst blocking
/// issue. Schema validity alone never makes a plan <see cref="Ready"/>.
/// </summary>
public enum PlanReadiness
{
    /// <summary>The JSON shape or the dependency DAG is broken: it cannot be executed at all.</summary>
    StructurallyInvalid,

    /// <summary>Executable, but something the worker needs is missing (steps, acceptance, requirement coverage, grounded files).</summary>
    Incomplete,

    /// <summary>The plan contradicts itself or the project's boundaries (unknown decisions, conflicting files, protected paths).</summary>
    ArchitecturallyInconsistent,

    Ready,
}

/// <summary>One finding. <see cref="Blocking"/> issues lower the readiness; the rest are advice shown to the user.</summary>
public sealed record PlanIssue(PlanReadiness Level, string Code, string? UnitId, string Message, bool Blocking = true)
{
    public override string ToString() => UnitId is null ? Message : $"{UnitId}: {Message}";
}

public sealed record PlanAssessment(PlanReadiness Readiness, IReadOnlyList<PlanIssue> Issues)
{
    public bool IsReady => Readiness == PlanReadiness.Ready;

    public IEnumerable<PlanIssue> Blocking => Issues.Where(i => i.Blocking);
}

/// <summary>
/// Deterministic engineering-quality checks on a plan, separate from (and after) schema validation. It never asks a
/// model: it checks what can be proven, such as whether files the plan claims exist really exist in the repository,
/// whether every requirement is covered and whether units name decisions that were never made.
/// </summary>
public static class PlanQuality
{
    public const int MaxStepsPerUnit = 15;
    public const int MaxFilesPerUnit = 10;

    /// <param name="repoFiles">Tracked files at the plan's base commit; null skips the grounding checks.</param>
    /// <param name="requireBlueprint">True when the plan was asked for as a blueprint: a plain contract is then incomplete.</param>
    /// <param name="protectedPaths">Globs no plan may target (the policy directory is always protected).</param>
    public static PlanAssessment Assess(
        Contract contract, IReadOnlySet<string>? repoFiles = null, bool requireBlueprint = false, IReadOnlyList<string>? protectedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var issues = new List<PlanIssue>();
        foreach (var error in ContractValidator.Validate(contract))
        {
            issues.Add(new(PlanReadiness.StructurallyInvalid, "structure", null, error));
        }

        if (issues.Count > 0)
        {
            return new PlanAssessment(PlanReadiness.StructurallyInvalid, issues);
        }

        var protectedGlobs = Supervisor.BuiltInProtectedPaths.Concat(protectedPaths ?? []).ToList();
        foreach (var unit in contract.Units)
        {
            foreach (var path in unit.ExpectedFiles)
            {
                if (PlanPaths.Normalize(path) is not { } p)
                {
                    issues.Add(new(PlanReadiness.ArchitecturallyInconsistent, "bad_path", unit.Id, $"'{path}' is not a relative path inside the repository"));
                }
                else if (Glob.AnyMatch(protectedGlobs, p))
                {
                    issues.Add(new(PlanReadiness.ArchitecturallyInconsistent, "protected_path", unit.Id, $"plans to change protected path '{p}'"));
                }
            }
        }

        if (!contract.IsBlueprint)
        {
            if (requireBlueprint)
            {
                issues.Add(new(PlanReadiness.Incomplete, "not_blueprint", null,
                    "the plan is a short contract, not a blueprint: analysis, architecture, decisions and implementation steps are missing"));
            }

            return Conclude(issues);
        }

        AssessBlueprint(contract, repoFiles, issues);
        return Conclude(issues);
    }

    private static void AssessBlueprint(Contract contract, IReadOnlySet<string>? repoFiles, List<PlanIssue> issues)
    {
        var decisionIds = contract.Decisions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var requirements = contract.Analysis?.Requirements ?? [];
        var requirementIds = requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        if (contract.Architecture is null || string.IsNullOrWhiteSpace(contract.Architecture.Summary))
        {
            issues.Add(new(PlanReadiness.Incomplete, "no_architecture", null, "the blueprint has no architecture summary"));
        }

        if (requirements.Count == 0)
        {
            issues.Add(new(PlanReadiness.Incomplete, "no_requirements", null, "the blueprint lists no requirements, so coverage cannot be checked"));
        }

        foreach (var duplicate in contract.Decisions.GroupBy(d => d.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            issues.Add(new(PlanReadiness.ArchitecturallyInconsistent, "duplicate_decision", null, $"decision id '{duplicate.Key}' is used twice"));
        }

        foreach (var decision in contract.Decisions.Where(d => string.IsNullOrWhiteSpace(d.Decision)))
        {
            issues.Add(new(PlanReadiness.Incomplete, "empty_decision", null, $"decision {decision.Id} does not say what was decided"));
        }

        var order = ContractValidator.Order(contract);
        var createdSoFar = new HashSet<string>(StringComparer.Ordinal);
        var creators = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var unit in order)
        {
            if (unit.Steps.Count == 0)
            {
                issues.Add(new(PlanReadiness.Incomplete, "no_steps", unit.Id, "has no implementation steps"));
            }
            else if (unit.Steps.Count > MaxStepsPerUnit)
            {
                issues.Add(new(PlanReadiness.Incomplete, "too_many_steps", unit.Id,
                    $"has {unit.Steps.Count} steps; a unit this large should be split (limit {MaxStepsPerUnit})", Blocking: false));
            }

            if (unit.Acceptance.Count == 0 && unit.Tests.Count == 0)
            {
                issues.Add(new(PlanReadiness.Incomplete, "no_acceptance", unit.Id, "has no acceptance criteria"));
            }

            if (unit.ExpectedFiles.Count == 0)
            {
                issues.Add(new(PlanReadiness.Incomplete, "no_files", unit.Id, "names no files to create or modify", Blocking: false));
            }
            else if (unit.ExpectedFiles.Count > MaxFilesPerUnit)
            {
                issues.Add(new(PlanReadiness.Incomplete, "too_many_files", unit.Id,
                    $"changes {unit.ExpectedFiles.Count} files; consider splitting it (limit {MaxFilesPerUnit})", Blocking: false));
            }

            foreach (var id in unit.Decisions.Where(id => !decisionIds.Contains(id)))
            {
                issues.Add(new(PlanReadiness.ArchitecturallyInconsistent, "unknown_decision", unit.Id, $"refers to decision '{id}', which the plan does not define"));
            }

            foreach (var id in unit.Requirements.Where(id => !requirementIds.Contains(id)))
            {
                issues.Add(new(PlanReadiness.ArchitecturallyInconsistent, "unknown_requirement", unit.Id, $"covers requirement '{id}', which the plan does not define"));
            }

            var modify = unit.Modify.Select(PlanPaths.Normalize).OfType<string>().ToList();
            var create = unit.Create.Select(PlanPaths.Normalize).OfType<string>().ToList();
            foreach (var path in modify.Intersect(create, StringComparer.Ordinal))
            {
                issues.Add(new(PlanReadiness.ArchitecturallyInconsistent, "create_and_modify", unit.Id, $"both creates and modifies '{path}'"));
            }

            foreach (var path in modify.Concat(create).Where(p => unit.Avoid.Any(a => PlanPaths.Covers(a, p))))
            {
                issues.Add(new(PlanReadiness.ArchitecturallyInconsistent, "avoid_conflict", unit.Id, $"plans to change '{path}' but also lists it under avoid"));
            }

            foreach (var path in create)
            {
                if (creators.TryGetValue(path, out var other))
                {
                    issues.Add(new(PlanReadiness.ArchitecturallyInconsistent, "created_twice", unit.Id, $"creates '{path}', which {other} already creates"));
                }
                else
                {
                    creators[path] = unit.Id;
                }
            }

            if (repoFiles is not null)
            {
                foreach (var path in modify.Where(p => !PlanPaths.Exists(repoFiles, p) && !createdSoFar.Contains(p)))
                {
                    issues.Add(new(PlanReadiness.Incomplete, "ungrounded_modify", unit.Id,
                        $"modifies '{path}', which does not exist in the repository and no earlier unit creates"));
                }

                foreach (var path in create.Where(p => repoFiles.Contains(p)))
                {
                    issues.Add(new(PlanReadiness.Incomplete, "create_existing", unit.Id, $"creates '{path}', which already exists (modify it instead?)", Blocking: false));
                }

                foreach (var reference in unit.References.Select(PlanPaths.Normalize).OfType<string>()
                             .Where(p => !PlanPaths.Exists(repoFiles, p) && !createdSoFar.Contains(p)))
                {
                    issues.Add(new(PlanReadiness.Incomplete, "ungrounded_reference", unit.Id,
                        $"references '{reference}', which does not exist in the repository"));
                }
            }

            foreach (var path in create)
            {
                createdSoFar.Add(path);
            }
        }

        foreach (var requirement in requirements.Where(r => !contract.Units.Any(u => u.Requirements.Contains(r.Id, StringComparer.Ordinal))))
        {
            issues.Add(new(PlanReadiness.Incomplete, "uncovered_requirement", null, $"requirement {requirement.Id} ({Shorten(requirement.Text)}) is not covered by any unit"));
        }

        if (repoFiles is not null)
        {
            foreach (var fact in contract.Analysis?.Facts ?? [])
            {
                foreach (var evidence in fact.Evidence.Select(PlanPaths.Normalize).OfType<string>().Where(p => !PlanPaths.Exists(repoFiles, p)))
                {
                    issues.Add(new(PlanReadiness.Incomplete, "ungrounded_fact", null,
                        $"the fact \"{Shorten(fact.Text)}\" cites '{evidence}', which does not exist in the repository"));
                }
            }
        }
    }

    private static PlanAssessment Conclude(List<PlanIssue> issues)
    {
        var blocking = issues.Where(i => i.Blocking).ToList();
        var readiness = blocking.Count == 0 ? PlanReadiness.Ready : blocking.Min(i => i.Level);
        return new PlanAssessment(readiness, issues);
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..77] + "...";
}

/// <summary>Path helpers for plan fields, which models write loosely (<c>./src/a.py</c>, <c>src\a.py</c>, <c>src/a.py:Foo</c>).</summary>
public static class PlanPaths
{
    /// <summary>A repo-relative forward-slash path, or null when the value is absolute, escapes the repo or is empty. Symbols after ':' or '#' are dropped.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim().Trim('`', '"', '\'').Replace('\\', '/');
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            return null; // a drive letter: absolute on Windows
        }

        var cut = path.IndexOfAny([':', '#', ' ']);
        if (cut > 0)
        {
            path = path[..cut];
        }

        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.StartsWith('/') || path.Length == 0 || path.Split('/').Any(s => s == ".."))
        {
            return null;
        }

        return path;
    }

    public static bool IsGlob(string path) => path.IndexOfAny(['*', '?', '[']) >= 0;

    /// <summary>True when <paramref name="path"/> is a tracked file, or a directory (or glob) that contains one.</summary>
    public static bool Exists(IReadOnlySet<string> repoFiles, string path)
    {
        if (repoFiles.Contains(path))
        {
            return true;
        }

        if (IsGlob(path))
        {
            return repoFiles.Any(f => Glob.IsMatch(path, f));
        }

        var dir = path.TrimEnd('/') + "/";
        return repoFiles.Any(f => f.StartsWith(dir, StringComparison.Ordinal));
    }

    /// <summary>True when an avoid entry (a file, a directory or a glob) covers <paramref name="path"/>.</summary>
    public static bool Covers(string avoid, string path)
    {
        if (Normalize(avoid) is not { } a)
        {
            return false;
        }

        if (IsGlob(a))
        {
            return Glob.IsMatch(a, path);
        }

        return string.Equals(a, path, StringComparison.Ordinal) || path.StartsWith(a.TrimEnd('/') + "/", StringComparison.Ordinal);
    }
}
