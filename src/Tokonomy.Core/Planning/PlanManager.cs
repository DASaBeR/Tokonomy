using System.Text.Json;
using System.Text.RegularExpressions;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;

namespace Tokonomy.Core.Planning;

public enum UnitProgress
{
    Pending,
    Running,
    Accepted,
    Failed,

    /// <summary>Dropped from the plan by a justified revision.</summary>
    Removed,

    /// <summary>Replaced by the smaller units it was split into.</summary>
    Superseded,
}

/// <summary>One field of one unit (or of the plan) before and after a revision. Values are JSON.</summary>
public sealed record PlanChange(string Op, string? UnitId, string? Field, string? Before, string? After);

/// <summary>
/// An approved change to the execution plan. Revision 1 is the original plan; every later revision records what it
/// changed, why, on what evidence and who decided.
/// </summary>
public sealed record PlanRevision
{
    public required int Number { get; init; }
    public required DateTimeOffset At { get; init; }

    /// <summary>Who asked for it: supervision, review, decomposition or user.</summary>
    public required string Source { get; init; }

    public required string Reason { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>The event that led here (for example <c>U2:blocked</c>).</summary>
    public string? Trigger { get; init; }

    /// <summary>The architect's diagnosis or decision, verbatim.</summary>
    public string? Decision { get; init; }

    public required IReadOnlyList<PlanChange> Changes { get; init; }

    /// <summary>True for changes a human must own (reopening completed work).</summary>
    public bool RequiresHumanApproval { get; init; }
}

public enum LessonScope
{
    /// <summary>Only the units listed in <see cref="Lesson.AppliesTo"/>.</summary>
    Units,

    /// <summary>Every remaining unit.</summary>
    Global,
}

/// <summary>
/// Something learned from a mistake or a discovery that later units must respect. A lesson with a
/// <see cref="ForbiddenPattern"/> is also enforced: a later diff that adds a line matching it fails the <c>lessons</c> gate.
/// </summary>
public sealed record Lesson
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public LessonScope Scope { get; init; } = LessonScope.Global;
    public IReadOnlyList<string> AppliesTo { get; init; } = [];

    /// <summary>A regular expression no line added by a later unit may match (checked deterministically).</summary>
    public string? ForbiddenPattern { get; init; }

    public string? SourceUnit { get; init; }
    public int Revision { get; init; }
}

/// <summary>Everything about a plan during execution; persisted as <c>plan-state.json</c>.</summary>
public sealed record PlanState
{
    public required Contract Original { get; init; }
    public required Contract Current { get; init; }
    public required int Revision { get; init; }
    public IReadOnlyList<PlanRevision> Revisions { get; init; } = [];
    public IReadOnlyList<Lesson> Lessons { get; init; } = [];
    public IReadOnlyDictionary<string, UnitProgress> Progress { get; init; } = new Dictionary<string, UnitProgress>();
}

/// <summary>Operation names a plan update may carry.</summary>
public static class PlanOps
{
    public const string UpdateUnit = "update_unit";
    public const string AddUnit = "add_unit";
    public const string RemoveUnit = "remove_unit";
    public const string SplitUnit = "split_unit";
    public const string AddDependency = "add_dependency";
    public const string RemoveDependency = "remove_dependency";
    public const string AddDont = "add_dont";
    public const string AddRule = "add_rule";
    public const string AddLesson = "add_lesson";

    public static readonly IReadOnlyList<string> All = [UpdateUnit, AddUnit, RemoveUnit, SplitUnit, AddDependency, RemoveDependency, AddDont, AddRule, AddLesson];
}

/// <summary>
/// One change in a plan update. List fields of <c>update_unit</c> are appended (deduplicated); <see cref="Spec"/>,
/// <see cref="Objective"/> and <see cref="Risk"/> replace; <see cref="Steps"/> replace only with <see cref="ReplaceSteps"/>.
/// </summary>
public sealed record PlanOperation
{
    public required string Op { get; init; }
    public string? Unit { get; init; }
    public string? Spec { get; init; }
    public string? Objective { get; init; }
    public IReadOnlyList<string> Steps { get; init; } = [];
    public bool ReplaceSteps { get; init; }
    public IReadOnlyList<string> Constraints { get; init; } = [];
    public IReadOnlyList<string> EdgeCases { get; init; } = [];
    public IReadOnlyList<string> Acceptance { get; init; } = [];
    public IReadOnlyList<string> Verification { get; init; } = [];
    public IReadOnlyList<string> Create { get; init; } = [];
    public IReadOnlyList<string> Modify { get; init; } = [];
    public IReadOnlyList<string> Avoid { get; init; } = [];
    public IReadOnlyList<string> References { get; init; } = [];
    public IReadOnlyList<string> Interfaces { get; init; } = [];
    public IReadOnlyList<string> Decisions { get; init; } = [];
    public IReadOnlyList<string> Corrections { get; init; } = [];
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public UnitRisk? Risk { get; init; }

    /// <summary>For <c>add_unit</c>.</summary>
    public WorkUnit? NewUnit { get; init; }

    /// <summary>For <c>split_unit</c>: the units that replace <see cref="Unit"/>, in order.</summary>
    public IReadOnlyList<WorkUnit> Parts { get; init; } = [];

    /// <summary>The text of a don't item, rule or lesson; for <c>remove_unit</c>, the justification.</summary>
    public string? Text { get; init; }

    /// <summary>For <c>add_lesson</c>: <c>global</c> or <c>units</c>.</summary>
    public string? Scope { get; init; }

    public IReadOnlyList<string> AppliesTo { get; init; } = [];
    public string? ForbiddenPattern { get; init; }
}

/// <summary>A proposed revision. <see cref="BaseRevision"/> must equal the current revision, or it is stale and rejected.</summary>
public sealed record PlanUpdate
{
    public required int BaseRevision { get; init; }
    public required string Source { get; init; }
    public required string Reason { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string? Trigger { get; init; }
    public string? Decision { get; init; }
    public required IReadOnlyList<PlanOperation> Operations { get; init; }
}

public sealed record PlanUpdateResult(bool Applied, int Revision, IReadOnlyList<string> Errors, PlanRevision? Record = null, IReadOnlyList<Lesson>? NewLessons = null)
{
    public static PlanUpdateResult Rejected(int revision, IReadOnlyList<string> errors) => new(false, revision, errors);
}

/// <summary>
/// Owns the execution plan of one run: the original plan (never changed), the current plan, the revision history,
/// unit progress and lessons. Every update is validated as a whole and applied atomically: a rejected update leaves
/// no trace except its errors. Completed units are immutable unless explicitly reopened.
/// </summary>
public sealed class PlanManager
{
    public const int MaxTextLength = 1000;
    public const int MaxPatternLength = 200;
    public const int MaxSplitParts = 5;

    private static readonly JsonSerializerOptions ChangeJson = new() { WriteIndented = false };

    private readonly Lock _gate = new();
    private readonly IClock _clock;
    private readonly IReadOnlyList<string> _protected;
    private readonly int _maxRevisions;
    private PlanState _state;

    /// <param name="protectedPaths">Globs plan updates may never target (the policy directory and the acceptance tests are added by the caller).</param>
    public PlanManager(Contract original, IReadOnlyList<string>? protectedPaths = null, int maxRevisions = 20, IClock? clock = null)
        : this(new PlanState
        {
            Original = original ?? throw new ArgumentNullException(nameof(original)),
            Current = original,
            Revision = 1,
            Progress = original.Units.ToDictionary(u => u.Id, _ => UnitProgress.Pending, StringComparer.Ordinal),
        }, protectedPaths, maxRevisions, clock)
    {
    }

    private PlanManager(PlanState state, IReadOnlyList<string>? protectedPaths, int maxRevisions, IClock? clock)
    {
        _state = state;
        _clock = clock ?? SystemClock.Instance;
        _protected = Supervisor.BuiltInProtectedPaths.Concat(protectedPaths ?? []).ToList();
        _maxRevisions = Math.Max(0, maxRevisions);
    }

    /// <summary>Continues from a saved state. Units that were running when the state was saved go back to pending.</summary>
    public static PlanManager Restore(PlanState state, IReadOnlyList<string>? protectedPaths = null, int maxRevisions = 20, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var progress = state.Progress.ToDictionary(p => p.Key, p => p.Value == UnitProgress.Running ? UnitProgress.Pending : p.Value, StringComparer.Ordinal);
        return new PlanManager(state with { Progress = progress }, protectedPaths, maxRevisions, clock);
    }

    public Contract Original => _state.Original;
    public Contract Current => _state.Current;
    public int Revision => _state.Revision;
    public IReadOnlyList<PlanRevision> Revisions => _state.Revisions;
    public IReadOnlyList<Lesson> Lessons => _state.Lessons;

    public PlanState Snapshot()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public UnitProgress ProgressOf(string unitId) => _state.Progress.TryGetValue(unitId, out var p) ? p : UnitProgress.Pending;

    /// <summary>The current version of a unit (it may have been revised since it was last read).</summary>
    public WorkUnit Unit(string unitId) =>
        Current.Units.FirstOrDefault(u => u.Id == unitId) ?? throw new KeyNotFoundException($"unit '{unitId}' is not in the current plan");

    public bool Contains(string unitId) => Current.Units.Any(u => u.Id == unitId);

    /// <summary>The next unit to run: the first pending unit in dependency order whose dependencies are all accepted.</summary>
    public WorkUnit? NextPending()
    {
        lock (_gate)
        {
            return ContractValidator.Order(Current).FirstOrDefault(u =>
                ProgressOf(u.Id) == UnitProgress.Pending && u.DependsOn.All(d => ProgressOf(d) == UnitProgress.Accepted));
        }
    }

    /// <summary>Units in the current plan that are still to do (pending or running), in dependency order.</summary>
    public IReadOnlyList<WorkUnit> Remaining() =>
        ContractValidator.Order(Current).Where(u => ProgressOf(u.Id) is UnitProgress.Pending or UnitProgress.Running).ToList();

    public void MarkRunning(string unitId) => SetProgress(unitId, UnitProgress.Running);

    public void MarkAccepted(string unitId) => SetProgress(unitId, UnitProgress.Accepted);

    public void MarkFailed(string unitId) => SetProgress(unitId, UnitProgress.Failed);

    /// <summary>Lessons that apply to a unit: global ones, and those naming the unit or the unit it was split from.</summary>
    public IReadOnlyList<Lesson> LessonsFor(string unitId)
    {
        var unit = Current.Units.FirstOrDefault(u => u.Id == unitId);
        return Lessons.Where(l => l.Scope == LessonScope.Global ||
                                  l.AppliesTo.Contains(unitId, StringComparer.Ordinal) ||
                                  (unit?.SplitFrom is { } parent && l.AppliesTo.Contains(parent, StringComparer.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// Puts an accepted unit back to pending. This is the only way completed work changes, it is never done on a
    /// model's say-so, and the revision is marked as requiring human approval.
    /// </summary>
    public PlanUpdateResult Reopen(string unitId, string reason, string source = "user")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (ProgressOf(unitId) != UnitProgress.Accepted || !Contains(unitId))
            {
                return PlanUpdateResult.Rejected(Revision, [$"unit '{unitId}' is not an accepted unit of the current plan"]);
            }

            var progress = new Dictionary<string, UnitProgress>(_state.Progress, StringComparer.Ordinal) { [unitId] = UnitProgress.Pending };
            var record = new PlanRevision
            {
                Number = Revision + 1,
                At = _clock.UtcNow,
                Source = source,
                Reason = reason,
                Changes = [new PlanChange("reopen_unit", unitId, "progress", Json(UnitProgress.Accepted.ToString()), Json(UnitProgress.Pending.ToString()))],
                RequiresHumanApproval = true,
            };
            _state = _state with { Revision = record.Number, Progress = progress, Revisions = [.. _state.Revisions, record] };
            return new PlanUpdateResult(true, record.Number, [], record, []);
        }
    }

    /// <summary>Validates the whole update against a copy of the plan and applies it only if every operation is valid.</summary>
    public PlanUpdateResult Apply(PlanUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            if (update.BaseRevision != Revision)
            {
                return PlanUpdateResult.Rejected(Revision, [$"stale update: it was made for revision {update.BaseRevision}, the plan is at revision {Revision}"]);
            }

            if (update.Operations.Count == 0)
            {
                return PlanUpdateResult.Rejected(Revision, ["the update has no operations"]);
            }

            if (_state.Revisions.Count >= _maxRevisions)
            {
                return PlanUpdateResult.Rejected(Revision, [$"revision limit reached ({_maxRevisions} per run)"]);
            }

            var draft = new Draft(_state);
            var errors = new List<string>();
            foreach (var (op, i) in update.Operations.Select((o, i) => (o, i)))
            {
                var before = errors.Count;
                ApplyOne(draft, op, errors);
                for (var e = before; e < errors.Count; e++)
                {
                    errors[e] = $"operation {i + 1} ({op.Op}{(op.Unit is null ? "" : " " + op.Unit)}): {errors[e]}";
                }
            }

            var contract = _state.Current with { Units = draft.Units, Rules = draft.Rules, DontList = draft.Dont };
            if (errors.Count == 0)
            {
                errors.AddRange(ContractValidator.Validate(contract).Select(e => $"the revised plan is invalid: {e}"));
            }

            if (errors.Count > 0)
            {
                return PlanUpdateResult.Rejected(Revision, errors);
            }

            var number = Revision + 1;
            var newLessons = draft.NewLessons.Select(l => l with { Revision = number }).ToList();
            var record = new PlanRevision
            {
                Number = number,
                At = _clock.UtcNow,
                Source = update.Source,
                Reason = update.Reason,
                Evidence = update.Evidence,
                Trigger = update.Trigger,
                Decision = update.Decision,
                Changes = draft.Changes,
            };
            _state = _state with
            {
                Current = contract,
                Revision = number,
                Revisions = [.. _state.Revisions, record],
                Lessons = [.. _state.Lessons, .. newLessons],
                Progress = draft.Progress,
            };
            return new PlanUpdateResult(true, number, [], record, newLessons);
        }
    }

    private void SetProgress(string unitId, UnitProgress progress)
    {
        lock (_gate)
        {
            var map = new Dictionary<string, UnitProgress>(_state.Progress, StringComparer.Ordinal) { [unitId] = progress };
            _state = _state with { Progress = map };
        }
    }

    private void ApplyOne(Draft draft, PlanOperation op, List<string> errors)
    {
        switch (op.Op)
        {
            case PlanOps.UpdateUnit:
                UpdateUnit(draft, op, errors);
                break;
            case PlanOps.AddUnit:
                AddUnit(draft, op, errors);
                break;
            case PlanOps.RemoveUnit:
                RemoveUnit(draft, op, errors);
                break;
            case PlanOps.SplitUnit:
                SplitUnit(draft, op, errors);
                break;
            case PlanOps.AddDependency or PlanOps.RemoveDependency:
                ChangeDependencies(draft, op, errors);
                break;
            case PlanOps.AddDont:
                if (CheckText(op.Text, errors) is { } dont && !draft.Dont.Contains(dont, StringComparer.Ordinal))
                {
                    draft.Dont.Add(dont);
                    draft.Changes.Add(new PlanChange(op.Op, null, "dont", null, Json(dont)));
                }

                break;
            case PlanOps.AddRule:
                if (CheckText(op.Text, errors) is { } rule && !draft.Rules.Any(r => r.Text == rule))
                {
                    var id = NextId("R", draft.Rules.Select(r => r.Id));
                    draft.Rules.Add(new Rule(id, rule));
                    draft.Changes.Add(new PlanChange(op.Op, null, "rules", null, Json($"{id}: {rule}")));
                }

                break;
            case PlanOps.AddLesson:
                AddLesson(draft, op, errors);
                break;
            default:
                errors.Add($"unknown operation; expected one of {string.Join(", ", PlanOps.All)}");
                break;
        }
    }

    private void UpdateUnit(Draft draft, PlanOperation op, List<string> errors)
    {
        if (draft.Mutable(op.Unit, errors) is not { } index)
        {
            return;
        }

        var unit = draft.Units[index];
        CheckPaths(op.Create.Concat(op.Modify), errors);
        CheckPathSyntax(op.Avoid.Concat(op.References), errors);
        foreach (var dep in op.DependsOn.Where(d => !draft.Units.Any(u => u.Id == d)))
        {
            errors.Add($"depends on unknown unit '{dep}'");
        }

        if (op.Spec is { } s && CheckText(s, errors, 8000) is null)
        {
            return;
        }

        var updated = unit with
        {
            Spec = string.IsNullOrWhiteSpace(op.Spec) ? unit.Spec : op.Spec.Trim(),
            Objective = string.IsNullOrWhiteSpace(op.Objective) ? unit.Objective : op.Objective.Trim(),
            Steps = op.ReplaceSteps && op.Steps.Count > 0 ? Clean(op.Steps) : Merge(unit.Steps, op.Steps),
            Constraints = Merge(unit.Constraints, op.Constraints),
            EdgeCases = Merge(unit.EdgeCases, op.EdgeCases),
            Acceptance = Merge(unit.Acceptance, op.Acceptance),
            Verification = Merge(unit.Verification, op.Verification),
            Create = Merge(unit.Create, op.Create.Select(PlanPaths.Normalize).OfType<string>()),
            Modify = Merge(unit.Modify, op.Modify.Select(PlanPaths.Normalize).OfType<string>()),
            Avoid = Merge(unit.Avoid, op.Avoid),
            References = Merge(unit.References, op.References),
            Interfaces = Merge(unit.Interfaces, op.Interfaces),
            Decisions = Merge(unit.Decisions, op.Decisions),
            Corrections = Merge(unit.Corrections, op.Corrections),
            DependsOn = Merge(unit.DependsOn, op.DependsOn),
            Risk = op.Risk ?? unit.Risk,
        };
        updated = updated with { Files = Merge(updated.Files, updated.Create.Concat(updated.Modify)) };
        draft.Units[index] = updated;
        draft.RecordDiff(op.Op, unit, updated);
    }

    private void AddUnit(Draft draft, PlanOperation op, List<string> errors)
    {
        if (op.NewUnit is not { } unit)
        {
            errors.Add("add_unit needs a unit");
            return;
        }

        if (draft.IdTaken(unit.Id))
        {
            errors.Add($"unit id '{unit.Id}' is already used");
            return;
        }

        if (unit.Tests.Count > 0)
        {
            errors.Add("acceptance tests are written only while planning; a unit added during the run cannot carry tests");
            return;
        }

        foreach (var dep in unit.DependsOn.Where(d => !draft.Units.Any(u => u.Id == d) || draft.Progress.GetValueOrDefault(d) == UnitProgress.Failed))
        {
            errors.Add($"depends on '{dep}', which is not a live unit of the plan");
        }

        CheckPaths(unit.Create.Concat(unit.Modify).Concat(unit.Files), errors);
        var clean = Normalized(unit);
        draft.Units.Add(clean);
        draft.Progress[clean.Id] = UnitProgress.Pending;
        draft.Changes.Add(new PlanChange(op.Op, clean.Id, "unit", null, Json(clean)));
    }

    private static void RemoveUnit(Draft draft, PlanOperation op, List<string> errors)
    {
        if (draft.Mutable(op.Unit, errors, allowRunning: false) is not { } index)
        {
            return;
        }

        var unit = draft.Units[index];
        if (string.IsNullOrWhiteSpace(op.Text))
        {
            errors.Add("removing a unit needs a justification (text)");
            return;
        }

        if (unit.Tests.Count > 0)
        {
            errors.Add("the unit carries acceptance tests; removing it would drop acceptance coverage");
            return;
        }

        draft.Units.RemoveAt(index);
        draft.Progress[unit.Id] = UnitProgress.Removed;
        draft.Changes.Add(new PlanChange(op.Op, unit.Id, "unit", Json(unit), Json(op.Text.Trim())));
        draft.Repoint(unit.Id, unit.DependsOn);
    }

    private void SplitUnit(Draft draft, PlanOperation op, List<string> errors)
    {
        if (draft.Mutable(op.Unit, errors) is not { } index)
        {
            return;
        }

        var original = draft.Units[index];
        if (original.SplitFrom is not null)
        {
            errors.Add($"'{original.Id}' was itself made by a split and is not split again");
            return;
        }

        if (op.Parts.Count is < 2 or > MaxSplitParts)
        {
            errors.Add($"a split needs 2 to {MaxSplitParts} parts, got {op.Parts.Count}");
            return;
        }

        var partIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in op.Parts)
        {
            if (draft.IdTaken(part.Id) || !partIds.Add(part.Id))
            {
                errors.Add($"part id '{part.Id}' is already used");
            }

            if (part.Tests.Count > 0)
            {
                errors.Add($"part '{part.Id}' carries tests; the original unit's tests move to the last part automatically");
            }

            CheckPaths(part.Create.Concat(part.Modify).Concat(part.Files), errors);
        }

        if (errors.Count > 0)
        {
            return;
        }

        var parts = new List<WorkUnit>();
        for (var i = 0; i < op.Parts.Count; i++)
        {
            var part = Normalized(op.Parts[i]);
            var allowed = original.DependsOn.Concat(parts.Select(p => p.Id)).ToHashSet(StringComparer.Ordinal);
            var declared = part.DependsOn.Where(allowed.Contains).ToList();
            if (i > 0 && declared.Count == 0)
            {
                declared.Add(parts[i - 1].Id);
            }

            part = part with
            {
                DependsOn = Merge(original.DependsOn, declared),
                SplitFrom = original.Id,
                Decisions = Merge(original.Decisions, part.Decisions),
                Avoid = Merge(original.Avoid, part.Avoid),
            };
            if (i == op.Parts.Count - 1)
            {
                // The original's definition of done moves to the last part, so acceptance coverage is never lost.
                part = part with
                {
                    Tests = original.Tests,
                    Acceptance = Merge(part.Acceptance, original.Acceptance),
                    Requirements = Merge(part.Requirements, original.Requirements),
                };
            }

            parts.Add(part);
        }

        draft.Units.RemoveAt(index);
        draft.Units.InsertRange(index, parts);
        draft.Progress[original.Id] = UnitProgress.Superseded;
        foreach (var part in parts)
        {
            draft.Progress[part.Id] = UnitProgress.Pending;
        }

        draft.Changes.Add(new PlanChange(op.Op, original.Id, "unit", Json(original), Json(parts)));
        draft.Repoint(original.Id, [parts[^1].Id]);
    }

    private static void ChangeDependencies(Draft draft, PlanOperation op, List<string> errors)
    {
        if (draft.Mutable(op.Unit, errors, allowRunning: false) is not { } index)
        {
            return;
        }

        if (op.DependsOn.Count == 0)
        {
            errors.Add("name at least one unit in depends_on");
            return;
        }

        var unit = draft.Units[index];
        WorkUnit updated;
        if (op.Op == PlanOps.AddDependency)
        {
            foreach (var dep in op.DependsOn.Where(d => !draft.Units.Any(u => u.Id == d)))
            {
                errors.Add($"unknown unit '{dep}'");
            }

            updated = unit with { DependsOn = Merge(unit.DependsOn, op.DependsOn) };
        }
        else
        {
            updated = unit with { DependsOn = unit.DependsOn.Where(d => !op.DependsOn.Contains(d, StringComparer.Ordinal)).ToList() };
        }

        draft.Units[index] = updated;
        draft.RecordDiff(op.Op, unit, updated);
    }

    private static void AddLesson(Draft draft, PlanOperation op, List<string> errors)
    {
        if (CheckText(op.Text, errors) is not { } text)
        {
            return;
        }

        var scope = (op.Scope ?? "global").Trim().ToLowerInvariant() switch
        {
            "global" => LessonScope.Global,
            "unit" or "units" => LessonScope.Units,
            var other => (LessonScope?)null,
        };
        if (scope is null)
        {
            errors.Add($"lesson scope '{op.Scope}' must be global or units");
            return;
        }

        var appliesTo = op.AppliesTo.Count > 0 ? op.AppliesTo.ToList() : op.Unit is { } u ? [u] : [];
        if (scope == LessonScope.Units && appliesTo.Count == 0)
        {
            errors.Add("a units-scoped lesson must name the units it applies to");
            return;
        }

        foreach (var id in appliesTo.Where(id => !draft.Units.Any(x => x.Id == id) && !draft.Progress.ContainsKey(id)))
        {
            errors.Add($"lesson applies to unknown unit '{id}'");
        }

        string? pattern = null;
        if (!string.IsNullOrWhiteSpace(op.ForbiddenPattern))
        {
            pattern = op.ForbiddenPattern.Trim();
            if (pattern.Length > MaxPatternLength)
            {
                errors.Add($"forbidden_pattern is longer than {MaxPatternLength} characters");
                return;
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException ex)
            {
                errors.Add($"forbidden_pattern is not a valid regular expression: {ex.Message}");
                return;
            }
        }

        var lesson = new Lesson
        {
            Id = NextId("L", draft.LessonIds),
            Text = text,
            Scope = scope.Value,
            AppliesTo = scope == LessonScope.Global ? [] : appliesTo,
            ForbiddenPattern = pattern,
            SourceUnit = op.Unit,
        };
        draft.NewLessons.Add(lesson);
        draft.Changes.Add(new PlanChange(op.Op, op.Unit, "lessons", null, Json(lesson)));
    }

    /// <summary>Paths the plan says will change: they must be inside the repository and never protected.</summary>
    private void CheckPaths(IEnumerable<string> paths, List<string> errors)
    {
        foreach (var raw in paths)
        {
            if (PlanPaths.Normalize(raw) is not { } path)
            {
                errors.Add($"'{raw}' is not a relative path inside the repository");
            }
            else if (Glob.AnyMatch(_protected, path))
            {
                errors.Add($"'{path}' is a protected path; a plan update may not target it");
            }
        }
    }

    private static void CheckPathSyntax(IEnumerable<string> paths, List<string> errors)
    {
        foreach (var raw in paths.Where(p => PlanPaths.Normalize(p) is null))
        {
            errors.Add($"'{raw}' is not a relative path inside the repository");
        }
    }

    private static string? CheckText(string? text, List<string> errors, int max = MaxTextLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add("text is empty");
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > max)
        {
            errors.Add($"text is longer than {max} characters");
            return null;
        }

        return trimmed;
    }

    private static WorkUnit Normalized(WorkUnit unit)
    {
        var create = unit.Create.Select(PlanPaths.Normalize).OfType<string>().ToList();
        var modify = unit.Modify.Select(PlanPaths.Normalize).OfType<string>().ToList();
        return unit with
        {
            Id = unit.Id.Trim(),
            Create = create,
            Modify = modify,
            Files = Merge(unit.Files.Select(PlanPaths.Normalize).OfType<string>().ToList(), create.Concat(modify)),
        };
    }

    private static List<string> Clean(IEnumerable<string> items) =>
        items.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();

    private static List<string> Merge(IEnumerable<string> existing, IEnumerable<string> added) => Clean(existing.Concat(added));

    private static string NextId(string prefix, IEnumerable<string> taken)
    {
        var set = taken.ToHashSet(StringComparer.Ordinal);
        var n = set.Count + 1;
        while (set.Contains($"{prefix}{n}"))
        {
            n++;
        }

        return $"{prefix}{n}";
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ChangeJson);

    /// <summary>A mutable copy of the plan an update is applied to; discarded unless every operation succeeds.</summary>
    private sealed class Draft(PlanState state)
    {
        public List<WorkUnit> Units { get; } = state.Current.Units.ToList();
        public List<Rule> Rules { get; } = state.Current.Rules.ToList();
        public List<string> Dont { get; } = state.Current.DontList.ToList();
        public Dictionary<string, UnitProgress> Progress { get; } = new(state.Progress, StringComparer.Ordinal);
        public List<PlanChange> Changes { get; } = [];
        public List<Lesson> NewLessons { get; } = [];

        public IEnumerable<string> LessonIds => state.Lessons.Select(l => l.Id).Concat(NewLessons.Select(l => l.Id));

        /// <summary>Ids used anywhere in this run's history (a removed or split unit's id is never reused).</summary>
        public bool IdTaken(string id) =>
            Units.Any(u => u.Id == id) || Progress.ContainsKey(id) || state.Original.Units.Any(u => u.Id == id);

        /// <summary>The index of a unit that may still change: pending, or the running unit when <paramref name="allowRunning"/>.</summary>
        public int? Mutable(string? unitId, List<string> errors, bool allowRunning = true)
        {
            var index = Units.FindIndex(u => u.Id == unitId);
            if (index < 0)
            {
                errors.Add($"unit '{unitId}' is not in the current plan");
                return null;
            }

            var progress = Progress.GetValueOrDefault(unitId!);
            if (progress == UnitProgress.Pending || (allowRunning && progress == UnitProgress.Running))
            {
                return index;
            }

            errors.Add(progress == UnitProgress.Accepted
                ? $"unit '{unitId}' is completed; completed units are immutable (reopen it explicitly)"
                : $"unit '{unitId}' is {progress.ToString().ToLowerInvariant()} and cannot change");
            return null;
        }

        /// <summary>Every unit that depended on <paramref name="replaced"/> now depends on <paramref name="with"/> instead.</summary>
        public void Repoint(string replaced, IReadOnlyList<string> with)
        {
            for (var i = 0; i < Units.Count; i++)
            {
                var unit = Units[i];
                if (unit.DependsOn.Contains(replaced, StringComparer.Ordinal))
                {
                    var updated = unit with { DependsOn = Merge(unit.DependsOn.Where(d => d != replaced), with) };
                    Units[i] = updated;
                    Changes.Add(new PlanChange("repoint_dependency", unit.Id, "depends_on", Json(unit.DependsOn), Json(updated.DependsOn)));
                }
            }
        }

        public void RecordDiff(string op, WorkUnit before, WorkUnit after)
        {
            void Field<T>(string name, T a, T b)
            {
                var x = Json(a);
                var y = Json(b);
                if (x != y)
                {
                    Changes.Add(new PlanChange(op, before.Id, name, x, y));
                }
            }

            Field("spec", before.Spec, after.Spec);
            Field("objective", before.Objective, after.Objective);
            Field("steps", before.Steps, after.Steps);
            Field("constraints", before.Constraints, after.Constraints);
            Field("edge_cases", before.EdgeCases, after.EdgeCases);
            Field("acceptance", before.Acceptance, after.Acceptance);
            Field("verification", before.Verification, after.Verification);
            Field("create", before.Create, after.Create);
            Field("modify", before.Modify, after.Modify);
            Field("avoid", before.Avoid, after.Avoid);
            Field("references", before.References, after.References);
            Field("interfaces", before.Interfaces, after.Interfaces);
            Field("decisions", before.Decisions, after.Decisions);
            Field("corrections", before.Corrections, after.Corrections);
            Field("depends_on", before.DependsOn, after.DependsOn);
            Field("risk", before.Risk.ToString(), after.Risk.ToString());
        }
    }
}
