namespace Tokonomy.Core.Model;

/// <summary>The architect's plan for a task: units, their dependency DAG, rules and acceptance checks.</summary>
public sealed record Contract
{
    public required string Task { get; init; }
    public required IReadOnlyList<WorkUnit> Units { get; init; }
    public IReadOnlyList<Rule> Rules { get; init; } = [];
    public IReadOnlyList<string> DontList { get; init; } = [];
}

public sealed record WorkUnit
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Spec { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> Files { get; init; } = [];
    public IReadOnlyList<string> Acceptance { get; init; } = [];

    /// <summary>
    /// Executable acceptance tests the architect wrote for this unit. They are placed in the worktree before the worker
    /// starts, the worker cannot change them, and the unit is accepted only if the acceptance command passes on all
    /// tests of this and earlier units.
    /// </summary>
    public IReadOnlyList<AcceptanceTestFile> Tests { get; init; } = [];
}

/// <summary>One test file written by the architect. <see cref="Path"/> is relative to the acceptance directory's parent (repo root).</summary>
public sealed record AcceptanceTestFile(string Path, string Content);

public sealed record Rule(string Id, string Text);
