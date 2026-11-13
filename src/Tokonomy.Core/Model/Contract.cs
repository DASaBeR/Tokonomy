namespace Tokonomy.Core.Model;

/// <summary>
/// The architect's plan for a task: units, their dependency DAG, rules and acceptance checks.
/// <para>
/// Schema version 1 is the original shape (units, rules, don't list). Version 2 is the engineering blueprint: the
/// architect's repository analysis, the architecture it intends, its decisions (ADRs) and detailed units with
/// implementation steps. Every version-2 field is optional on the wire, so plans saved by older builds still load.
/// </para>
/// </summary>
public sealed record Contract
{
    public const int BlueprintSchemaVersion = 2;

    public required string Task { get; init; }
    public required IReadOnlyList<WorkUnit> Units { get; init; }
    public IReadOnlyList<Rule> Rules { get; init; } = [];
    public IReadOnlyList<string> DontList { get; init; } = [];

    /// <summary>1: the original contract. 2: an engineering blueprint (analysis, architecture, decisions, detailed units).</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>What the architect found in the repository and the requirements it derived (version 2).</summary>
    public PlanAnalysis? Analysis { get; init; }

    /// <summary>The implementation architecture the units must follow (version 2).</summary>
    public ArchitectureDesign? Architecture { get; init; }

    /// <summary>Decisions the worker must not re-open (version 2).</summary>
    public IReadOnlyList<ArchitectureDecision> Decisions { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBlueprint => SchemaVersion >= BlueprintSchemaVersion;
}

public sealed record WorkUnit
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Spec { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Every file the unit is expected to change (for a blueprint: <see cref="Create"/> plus <see cref="Modify"/>).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    public IReadOnlyList<string> Acceptance { get; init; } = [];

    /// <summary>
    /// Executable acceptance tests the architect wrote for this unit. They are placed in the worktree before the worker
    /// starts, the worker cannot change them, and the unit is accepted only if the acceptance command passes on all
    /// tests of this and earlier units.
    /// </summary>
    public IReadOnlyList<AcceptanceTestFile> Tests { get; init; } = [];

    // ---- Blueprint (schema version 2) fields. All optional. ----

    /// <summary>One sentence: what this unit achieves.</summary>
    public string? Objective { get; init; }

    /// <summary>Files the unit creates.</summary>
    public IReadOnlyList<string> Create { get; init; } = [];

    /// <summary>Existing files the unit modifies. Each must exist in the repository (or be created by an earlier unit).</summary>
    public IReadOnlyList<string> Modify { get; init; } = [];

    /// <summary>Files, directories or globs the unit must not touch. Touching one is a plan deviation.</summary>
    public IReadOnlyList<string> Avoid { get; init; } = [];

    /// <summary>Existing code to read and reuse (paths, optionally <c>path:Symbol</c>).</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>Implementation steps, in execution order.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>Interfaces, signatures and contracts the unit must provide or use, exactly as written.</summary>
    public IReadOnlyList<string> Interfaces { get; init; } = [];

    /// <summary>Ids of the architecture decisions (ADRs) that apply to this unit.</summary>
    public IReadOnlyList<string> Decisions { get; init; } = [];

    public IReadOnlyList<string> Constraints { get; init; } = [];
    public IReadOnlyList<string> EdgeCases { get; init; } = [];

    /// <summary>Ids of the requirements this unit covers.</summary>
    public IReadOnlyList<string> Requirements { get; init; } = [];

    /// <summary>How the worker can check its own work. Informational: Tokonomy never executes these.</summary>
    public IReadOnlyList<string> Verification { get; init; } = [];

    public UnitRisk Risk { get; init; } = UnitRisk.Low;

    public IReadOnlyList<string> SideEffects { get; init; } = [];

    /// <summary>Corrections the architect made to this unit's instructions during the run (newest last).</summary>
    public IReadOnlyList<string> Corrections { get; init; } = [];

    /// <summary>Set on units created by splitting an oversized unit: the id of the unit they replace.</summary>
    public string? SplitFrom { get; init; }

    /// <summary>Every path the unit plans to change: <see cref="Files"/>, <see cref="Create"/> and <see cref="Modify"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ExpectedFiles => Files.Concat(Create).Concat(Modify).Distinct(StringComparer.Ordinal).ToList();
}

public enum UnitRisk
{
    Low,
    Medium,
    High,
}

/// <summary>One test file written by the architect. <see cref="Path"/> is relative to the acceptance directory's parent (repo root).</summary>
public sealed record AcceptanceTestFile(string Path, string Content);

public sealed record Rule(string Id, string Text);

/// <summary>The architect's reading of the repository and of the task.</summary>
public sealed record PlanAnalysis
{
    /// <summary>Facts about the repository, each backed by files that prove it. Tokonomy checks the files exist.</summary>
    public IReadOnlyList<RepoFact> Facts { get; init; } = [];

    /// <summary>What the architect assumed without proof. Kept separate from facts on purpose.</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = [];

    public IReadOnlyList<Requirement> Requirements { get; init; } = [];

    /// <summary>Existing behaviour the change must keep.</summary>
    public IReadOnlyList<string> Preserve { get; init; } = [];

    public IReadOnlyList<string> Risks { get; init; } = [];

    /// <summary>Questions the architect could not resolve and the assumption it made for each.</summary>
    public IReadOnlyList<string> OpenQuestions { get; init; } = [];
}

public sealed record RepoFact(string Text, IReadOnlyList<string> Evidence);

/// <summary>A requirement the plan must cover. <see cref="Kind"/>: functional, non_functional, edge_case, security, performance, compatibility.</summary>
public sealed record Requirement(string Id, string Text, string Kind = "functional");

public sealed record ArchitectureDesign
{
    public string Summary { get; init; } = "";
    public IReadOnlyList<ComponentDesign> Components { get; init; } = [];

    /// <summary>Statements that must hold after every unit (for example "no module-level mutable state").</summary>
    public IReadOnlyList<string> Invariants { get; init; } = [];

    public string? ErrorHandling { get; init; }
    public string? Security { get; init; }
    public string? Performance { get; init; }
}

/// <summary>A component the plan creates or modifies. <see cref="Change"/>: create or modify.</summary>
public sealed record ComponentDesign(string Name, string Path, string Change, string Responsibility);

/// <summary>An architecture decision record: settled by the architect so the worker does not decide it.</summary>
public sealed record ArchitectureDecision
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Decision { get; init; }
    public string Rationale { get; init; } = "";
    public IReadOnlyList<string> AlternativesRejected { get; init; } = [];
    public IReadOnlyList<string> Constraints { get; init; } = [];
}
