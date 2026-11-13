using System.Text.Json;
using Tokonomy.Core.Planning;

namespace Tokonomy.Core.Supervision;

public enum WorkerReportStatus
{
    Completed,
    Blocked,
    ClarificationRequired,
    UnexpectedDiscovery,
    Failed,
}

/// <summary>
/// What the worker says about its attempt, read from <see cref="WorkerReports.FileName"/>. It is a signal for
/// supervision, never proof: a "completed" report does not accept a unit (the diff and the gates do), and evidence
/// paths the worker names are checked against the worktree.
/// </summary>
public sealed record WorkerReport
{
    public required WorkerReportStatus Status { get; init; }

    /// <summary>A short machine-friendly reason, e.g. <c>missing_dependency</c>, <c>missing_interface</c>, <c>plan_conflict</c>.</summary>
    public string? Reason { get; init; }

    public string Description { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> AttemptedActions { get; init; } = [];
    public IReadOnlyList<string> Discoveries { get; init; } = [];
    public bool DecisionRequired { get; init; }

    /// <summary>Problems Tokonomy found with the report itself (unknown fields, evidence that does not exist).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>The worker cannot or should not continue without an architect decision.</summary>
    public bool NeedsArchitect => Status is WorkerReportStatus.Blocked or WorkerReportStatus.ClarificationRequired || DecisionRequired;

    public bool HasDiscovery => Status == WorkerReportStatus.UnexpectedDiscovery || Discoveries.Count > 0;

    public string Summary =>
        $"{Status}{(Reason is null ? "" : $" ({Reason})")}: {(Description.Length <= 300 ? Description : Description[..297] + "...")}";
}

public static class WorkerReports
{
    /// <summary>The report file at the worktree root. Tokonomy reads and removes it before the diff is taken.</summary>
    public const string FileName = "tokonomy-report.json";

    public const int MaxBytes = 16_384;
    private const int MaxItems = 10;
    private const int MaxChars = 2000;

    /// <summary>
    /// Parses a report leniently (models get field names slightly wrong) but validates what matters. Returns null when
    /// the text is not a JSON object or has no recognizable status.
    /// </summary>
    /// <param name="exists">Checks a repo-relative path exists in the worktree; null skips the evidence check.</param>
    public static WorkerReport? Parse(string text, Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxBytes)
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = doc.RootElement;
            var status = ParseStatus(Str(root, "status"));
            if (status is null)
            {
                return null;
            }

            var problems = new List<string>();
            var evidence = List(root, "evidence");
            if (exists is not null)
            {
                foreach (var path in evidence)
                {
                    if (PlanPaths.Normalize(path) is not { } p)
                    {
                        problems.Add($"evidence '{path}' is not a repository path");
                    }
                    else if (!exists(p))
                    {
                        problems.Add($"evidence '{p}' does not exist in the worktree");
                    }
                }
            }

            return new WorkerReport
            {
                Status = status.Value,
                Reason = Str(root, "reason"),
                Description = Str(root, "description") ?? Str(root, "summary") ?? "",
                Evidence = evidence,
                AttemptedActions = List(root, "attempted_actions"),
                Discoveries = List(root, "discoveries"),
                DecisionRequired = root.TryGetProperty("decision_required", out var d) && d.ValueKind == JsonValueKind.True,
                Problems = problems,
            };
        }
    }

    internal static WorkerReportStatus? ParseStatus(string? value) => value?.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
    {
        "completed" or "complete" or "done" or "success" => WorkerReportStatus.Completed,
        "blocked" => WorkerReportStatus.Blocked,
        "clarification_required" or "needs_clarification" or "clarification" => WorkerReportStatus.ClarificationRequired,
        "unexpected_discovery" or "discovery" => WorkerReportStatus.UnexpectedDiscovery,
        "failed" or "failure" => WorkerReportStatus.Failed,
        _ => null,
    };

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? Cap(v.GetString()!.Trim())
            : null;

    private static List<string> List(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v))
        {
            return [];
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(v.GetString()) ? [] : [Cap(v.GetString()!.Trim())];
        }

        return v.ValueKind != JsonValueKind.Array
            ? []
            : v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                .Select(e => Cap(e.GetString()!.Trim())).Take(MaxItems).ToList();
    }

    private static string Cap(string s) => s.Length <= MaxChars ? s : s[..MaxChars] + "...";
}
