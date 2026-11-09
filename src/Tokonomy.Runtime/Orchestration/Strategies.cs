using Tokonomy.Core.Model;

namespace Tokonomy.Runtime.Orchestration;

/// <summary>A way of completing a task. The benchmark compares these on cost per successful task.</summary>
public sealed record Strategy(
    string Id,
    string Description,
    bool UsesArchitectPlan,
    bool UseGates,
    bool AllowEscalation,
    int? MaxAttempts,
    bool WorkerIsStrong)
{
    public static readonly Strategy StrongOnly = new("1", "strong model only, single shot", false, false, false, 1, true);
    public static readonly Strategy StrongWithGates = new("1b", "strong model + gates + retry", false, true, false, null, true);
    public static readonly Strategy CheapOnly = new("2", "cheap model only, single shot", false, false, false, 1, false);
    public static readonly Strategy SplitNoGates = new("3", "architect plan + cheap worker, no gates", true, false, false, 1, false);
    public static readonly Strategy Supervised = new("4", "architect plan + cheap worker + gates + supervision", true, true, true, null, false);

    public static IReadOnlyList<Strategy> All { get; } = [StrongOnly, StrongWithGates, CheapOnly, SplitNoGates, Supervised];

    public static Strategy Parse(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown strategy '{id}'. Expected one of: {string.Join(", ", All.Select(s => s.Id))}.");

    /// <summary>Single-unit contract for strategies without an architect plan: the task text is the spec.</summary>
    public static Contract SingleUnit(string task) => new()
    {
        Task = task,
        Units = [new WorkUnit { Id = "U1", Title = "Complete the task", Spec = task }],
    };
}
