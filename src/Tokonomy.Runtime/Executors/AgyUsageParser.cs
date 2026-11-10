using System.Text.Json;
using System.Text.RegularExpressions;
using Tokonomy.Core.Model;

namespace Tokonomy.Runtime.Executors;

/// <summary>One tool call the agent made, from the <c>stream-json</c> events.</summary>
public sealed record AgyToolCall(string Name, string? Target, string State, string? Cwd = null)
{
    public bool IsCommand => Name == "run_command";

    /// <summary>The step ended in <c>ERROR</c>: the call was refused (permission rule) or failed.</summary>
    public bool Errored => string.Equals(State, "ERROR", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Everything Tokonomy reads from one <c>agy -p --output-format stream-json|json</c> run.</summary>
public sealed record AgyRunReport
{
    public bool HasResult { get; init; }
    public string? ConversationId { get; init; }

    /// <summary>The model the session was actually started with (the <c>init</c> event). Null for plain <c>json</c> output.</summary>
    public string? Model { get; init; }

    /// <summary><c>SUCCESS</c>, <c>ERROR</c>, ...</summary>
    public string? Status { get; init; }

    public string? Error { get; init; }
    public string Response { get; init; } = "";
    public IReadOnlyList<WorkerUsage> Usage { get; init; } = [];

    /// <summary>Entries of <c>denied_actions</c>: tool calls headless mode auto-denied because it cannot prompt.</summary>
    public IReadOnlyList<string> DeniedActions { get; init; } = [];

    public IReadOnlyList<AgyToolCall> Tools { get; init; } = [];
}

/// <summary>Parses agy's JSON output. Tolerant of noise lines; returns <c>HasResult = false</c> when no result was found.</summary>
public static class AgyRunParser
{
    public static AgyRunReport Parse(string stdout, string? requestedModel = null)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new AgyRunReport();
        }

        var report = new AgyRunReport();
        var tools = new List<AgyToolCall>();
        var seenTools = new Dictionary<(string Conversation, int Step), int>();

        // Plain json output is one (possibly multi-line) object; stream-json is one object per line.
        if (TryParse(stdout.Trim(), out var whole) && whole.RootElement.ValueKind == JsonValueKind.Object && !whole.RootElement.TryGetProperty("event", out _))
        {
            using (whole)
            {
                return FromResult(report, whole.RootElement, requestedModel) with { Tools = [] };
            }
        }

        whole?.Dispose();

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(line, out var doc))
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var kind = Str(root, "event");
                switch (kind)
                {
                    case "init" when root.TryGetProperty("init", out var init) && init.ValueKind == JsonValueKind.Object:
                        report = report with { Model = Str(init, "model"), ConversationId = Str(root, "conversation_id") ?? report.ConversationId };
                        break;

                    case "step_update" when root.TryGetProperty("step_update", out var step) && step.ValueKind == JsonValueKind.Object
                                            && Str(step, "step_type") == "tool":
                        RecordTool(step, tools, seenTools);
                        break;

                    case "result" when root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object:
                        report = FromResult(report, result, requestedModel);
                        break;
                }
            }
        }

        return report with { Tools = tools };
    }

    private static void RecordTool(JsonElement step, List<AgyToolCall> tools, Dictionary<(string, int), int> seen)
    {
        var name = Str(step, "tool_name") ?? "unknown";
        var state = Str(step, "state") ?? "";
        string? target = null;
        if (step.TryGetProperty("tool_info", out var info) && info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in TargetKeys)
            {
                if (Str(p, key) is { Length: > 0 } value)
                {
                    target = value;
                    break;
                }
            }
        }

        string? cwd = null;
        if (step.TryGetProperty("tool_info", out var ti) && ti.ValueKind == JsonValueKind.Object &&
            ti.TryGetProperty("parameters", out var tp) && tp.ValueKind == JsonValueKind.Object)
        {
            cwd = Str(tp, "Cwd");
        }

        var key2 = (Str(step, "conversation_id") ?? "", step.TryGetProperty("step_index", out var idx) && idx.ValueKind == JsonValueKind.Number ? idx.GetInt32() : -1);
        var call = new AgyToolCall(name, target, state, cwd);
        if (key2.Item2 >= 0 && seen.TryGetValue(key2, out var at))
        {
            // The same step reports ACTIVE, then DONE or ERROR: keep the final state, and the target if only the first had one.
            tools[at] = call with { Target = call.Target ?? tools[at].Target, Cwd = call.Cwd ?? tools[at].Cwd };
        }
        else
        {
            if (key2.Item2 >= 0)
            {
                seen[key2] = tools.Count;
            }

            tools.Add(call);
        }
    }

    private static readonly string[] TargetKeys = ["CommandLine", "Path", "AbsolutePath", "TargetFile", "FilePath", "DirectoryPath", "SearchPath", "Url"];

    private static AgyRunReport FromResult(AgyRunReport report, JsonElement result, string? requestedModel)
    {
        var usage = new List<WorkerUsage>();
        if (result.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            // The result does not name a model, so label usage with the one the session reported, then the requested one.
            var label = report.Model ?? requestedModel;
            usage.Add(new WorkerUsage(
                string.IsNullOrWhiteSpace(label) ? "agy" : label,
                Long(u, "input_tokens"),
                Long(u, "output_tokens") + Long(u, "thinking_tokens"),
                Long(u, "cache_read_tokens"),
                null));
        }

        var denied = new List<string>();
        if (result.TryGetProperty("denied_actions", out var d) && d.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in d.EnumerateArray())
            {
                var action = Str(item, "action");
                var display = Str(item, "display_name");
                denied.Add(string.Join(": ", new[] { action, display }.Where(x => !string.IsNullOrEmpty(x))));
            }
        }

        return report with
        {
            HasResult = true,
            ConversationId = Str(result, "conversation_id") is { Length: > 0 } id ? id : report.ConversationId,
            Status = Str(result, "status"),
            Error = Str(result, "error"),
            Response = Str(result, "response") ?? "",
            Usage = usage,
            DeniedActions = denied,
        };
    }

    private static bool TryParse(string text, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
}

/// <summary>Back-compat shim: usage only (the model label comes from the run when the output does not name one).</summary>
public static class AgyUsageParser
{
    public static IReadOnlyList<WorkerUsage> Parse(string stdout, string? model) => AgyRunParser.Parse(stdout, model).Usage;
}

/// <summary>
/// Decides whether an agy run can be trusted. A run is faulted when ANY of these holds, because agy reports most of
/// them with exit code 0 and <c>status: SUCCESS</c>:
/// <list type="bullet">
/// <item>non-zero exit, no parsable result, or a non-success status;</item>
/// <item>the session started on a different model than requested (or does not say which);</item>
/// <item><c>denied_actions</c> is non-empty (headless auto-deny; the session ends at the first one);</item>
/// <item>an explicit deny rule refused a call (the step ends in <c>ERROR</c>; <c>denied_actions</c> stays empty);</item>
/// <item>the agent ran, or tried to run, a shell command outside the allowlist;</item>
/// <item>a file tool was pointed outside the worktree and errored;</item>
/// <item>the response says the call was blocked by a permission rule.</item>
/// </list>
/// </summary>
public static partial class AgyRunInterpreter
{
    public static WorkerFault? Evaluate(
        AgyRunReport report, int exitCode, string stderr, string? requestedModel, bool verifyModel, string workdir,
        IReadOnlyCollection<string> allowedCommands, IReadOnlyCollection<string>? deniedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(allowedCommands);

        // agy refuses an unknown model outright (status ERROR, exit 1, no session started). That is a permanent
        // configuration error: never retried, and never answered by quietly substituting another model.
        if (report.HasResult && string.IsNullOrWhiteSpace(report.Model) && !IsSuccess(report) && InvalidModel().IsMatch(report.Error ?? ""))
        {
            return new(WorkerFaultKind.ModelMismatch, $"agy rejected the requested model '{requestedModel}': {Clip(report.Error!, 300)}");
        }

        if (verifyModel && !string.IsNullOrWhiteSpace(requestedModel))
        {
            if (string.IsNullOrWhiteSpace(report.Model))
            {
                // Only meaningful for a result that claims success: with no output, or an error status, the real problem
                // is reported below instead.
                if (report.HasResult && IsSuccess(report))
                {
                    return new(WorkerFaultKind.ModelMismatch,
                        $"requested '{requestedModel}' but the run did not report which model it used (use --output-format stream-json)");
                }
            }
            else if (!ModelsMatch(requestedModel, report.Model))
            {
                return new(WorkerFaultKind.ModelMismatch, $"requested '{requestedModel}' but the run used '{report.Model}'");
            }
        }

        if (!report.HasResult)
        {
            return exitCode != 0
                ? new(WorkerFaultKind.ProcessFailed, $"exit code {exitCode} and no result: {Tail(stderr)}")
                : new(WorkerFaultKind.NoResult, $"exit code 0 but no JSON result was printed: {Tail(stderr)}");
        }

        var allowed = new HashSet<string>(allowedCommands.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var outside = report.Tools
            .Where(t => t.IsCommand && t.Target is not null && !allowed.Contains(Normalize(t.Target)))
            .Select(t => t.Target!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (report.DeniedActions.Count > 0 || outside.Count > 0)
        {
            var parts = new List<string>();
            if (outside.Count > 0)
            {
                parts.Add("command(s) outside the allowlist: " + string.Join(" | ", outside.Select(c => Clip(c, 160))));
            }

            if (report.DeniedActions.Count > 0)
            {
                parts.Add("auto-denied: " + string.Join(", ", report.DeniedActions));
            }

            parts.Add(allowed.Count == 0
                ? "no shell commands are permitted; use the file tools"
                : "permitted commands: " + string.Join(" | ", allowed.Order(StringComparer.OrdinalIgnoreCase)));
            return new(WorkerFaultKind.PermissionDenied, string.Join("; ", parts));
        }

        // agy 1.5.3 rejects a Cwd argument, so commands run in the worktree. Should a later version accept it, an allowed
        // command run elsewhere would execute someone else's code: that is a fault, not a success.
        var elsewhere = report.Tools.FirstOrDefault(t => t.IsCommand && !string.IsNullOrWhiteSpace(t.Cwd) && !Inside(workdir, t.Cwd!) &&
                                                         !Inside(workdir, Path.Combine(t.Cwd!, "x")));
        if (elsewhere is not null)
        {
            return new(WorkerFaultKind.PermissionDenied, $"a command was run outside the worktree (Cwd {Clip(elsewhere.Cwd!, 200)}): {Clip(elsewhere.Target ?? "", 160)}");
        }

        var escaped = report.Tools.FirstOrDefault(t => t.Errored && !t.IsCommand && t.Target is not null && IsPath(t.Name) && !Inside(workdir, t.Target));
        // An explicit deny rule refuses with an ERROR step and no denied_actions entry. Inside the worktree that looks
        // like an ordinary tool failure unless we compare against the rules we wrote (e.g. .git/hooks, a denied link).
        escaped ??= report.Tools.FirstOrDefault(t => t.Errored && !t.IsCommand && t.Target is not null && IsPath(t.Name) &&
                                                     (deniedPaths ?? []).Any(d => SameOrInside(d, t.Target, workdir)));
        if (escaped is not null)
        {
            return new(WorkerFaultKind.PermissionDenied, $"{escaped.Name} was refused by a permission rule: {Clip(escaped.Target!, 200)}");
        }

        if (!string.Equals(report.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return new(WorkerFaultKind.ReportedError, $"status {report.Status ?? "(none)"}: {Clip(report.Error ?? stderr, 400)}");
        }

        if (exitCode != 0)
        {
            return new(WorkerFaultKind.ProcessFailed, $"exit code {exitCode}: {Tail(stderr)}");
        }

        if (BlockedText().IsMatch(report.Response))
        {
            return new(WorkerFaultKind.PermissionDenied, $"the agent reports a permission rule blocked a call: {Clip(report.Response, 300)}");
        }

        // A tool refused by an explicit deny rule ends in ERROR with an otherwise normal result; a non-command, in-worktree
        // ERROR is an ordinary tool failure (e.g. an edit whose text did not match) the agent can recover from.
        return null;
    }

    /// <summary>Exact model id match, case-insensitive. No aliasing: a different id is a different model.</summary>
    public static bool ModelsMatch(string requested, string actual) =>
        string.Equals(requested.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccess(AgyRunReport report) => string.Equals(report.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

    private static bool IsPath(string tool) => tool is "write_file" or "edit_file" or "read_file" or "write_to_file" or "replace_file_content" or "multi_replace_file_content";

    private static bool SameOrInside(string denied, string path, string workdir)
    {
        try
        {
            var full = Path.GetFullPath(path, workdir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var d = Path.GetFullPath(denied).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, d, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || Inside(d, full);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool Inside(string root, string path)
    {
        try
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var p = Path.GetFullPath(path, root);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return p.StartsWith(r, cmp) || string.Equals(p + Path.DirectorySeparatorChar, r, cmp);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static string Normalize(string command) => WhiteSpace().Replace(command.Trim(), " ");

    private static string Tail(string s)
    {
        s = (s ?? "").Trim();
        return s.Length <= 400 ? s : "..." + s[^400..];
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"invalid model|not recognized as a known model|unknown model|model .* not (found|available)", RegexOptions.IgnoreCase)]
    private static partial Regex InvalidModel();

    // Phrases agy itself uses when it refuses a call. Deliberately not a bare "permission denied": a worker may
    // legitimately mention that in a summary of unrelated code.
    [GeneratedRegex(@"blocked by a user-configured permission rule|Matches user-configured deny rule|permission check failed for|auto-denied|headless mode cannot prompt", RegexOptions.IgnoreCase)]
    private static partial Regex BlockedText();
}
