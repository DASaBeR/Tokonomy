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
}

public sealed record Rule(string Id, string Text);
