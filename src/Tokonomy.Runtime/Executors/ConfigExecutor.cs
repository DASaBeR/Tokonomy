using System.Text;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Net;
using Tokonomy.Runtime.Gates;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Executors;

/// <summary>Per-run values an executor needs: where the metering proxy is, and where logs go.</summary>
public sealed record ExecutorSession
{
    /// <summary>OpenAI-style base URL ending in <c>/v1</c>, or empty when no proxy runs.</summary>
    public string ProxyUrl { get; init; } = "";

    /// <summary>Bare proxy origin (for clients that append <c>/v1/...</c> themselves).</summary>
    public string ProxyRoot { get; init; } = "";

    public string ProxyToken { get; init; } = "";
    public required string LogDirectory { get; init; }
    public IReadOnlyList<string> ExtraAllowedEnv { get; init; } = [];

    /// <summary>The policy's command gates as exact command lines; an executor may allow the worker to run the same checks.</summary>
    public IReadOnlyList<string> GateCommands { get; init; } = [];

    /// <summary>
    /// The shell commands the user approved for the unit this session works on (per-unit approval). It never narrows
    /// <c>permissions.allow_all_commands</c>, which allows everything for every unit.
    /// </summary>
    public ShellGrant ShellGrant { get; init; } = ShellGrant.None;

    /// <summary>
    /// Paths inside the worktree the worker must not touch, evaluated at the start of every attempt (they change as
    /// prepare steps and gates run): links that lead outside the worktree, and git-ignored content.
    /// </summary>
    public Func<IReadOnlyList<PathRestriction>>? Restrictions { get; init; }
}

/// <summary>A worktree-relative path the worker may not write (and, with <see cref="DenyRead"/>, not read either).</summary>
public sealed record PathRestriction(string RelativePath, bool DenyRead, string Reason);

/// <summary>
/// Runs a coding-agent CLI described by <c>executors.yaml</c>. The brief is written to the agent's native
/// instructions file (appended to any existing content) and every file Tokonomy writes is restored afterwards,
/// so none of it shows up in the unit's diff.
/// </summary>
public sealed class ConfigExecutor(string name, ExecutorDefinition definition, ExecutorSession session, IProcessRunner runner) : IExecutor
{
    public const string BriefMarker = "<!-- tokonomy:brief -->";

    public string Name => name;

    public ExecutorCapabilities Capabilities => definition.Capabilities;

    public async Task<RunResult> RunAsync(Brief brief, string workdir, string? model, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var restore = new List<(string Relative, string? Original)>();
        RunResult result;
        try
        {
            result = await RunCoreAsync(brief, workdir, model, timeout, restore, ct);
        }
        catch (WorktreeEscapeException ex)
        {
            Restore(workdir, restore);
            return new RunResult
            {
                ExitCode = -1,
                Duration = TimeSpan.Zero,
                Fault = new WorkerFault(WorkerFaultKind.WorkspaceViolation, $"the worktree was not safe to use: {ex.Message}"),
            };
        }
        catch
        {
            Restore(workdir, restore);
            throw;
        }

        // Put back what Tokonomy wrote. If the worker replaced one of those files with a link, the link is removed (never
        // written through) and the attempt is a workspace violation, whatever else it reported.
        var tampering = Restore(workdir, restore);
        if (tampering.Count > 0)
        {
            result = result with { Fault = new WorkerFault(WorkerFaultKind.WorkspaceViolation, string.Join("; ", tampering)) };
        }

        // Fault text comes from the agent's stderr and travels into the ledger, the transcript and the next brief.
        return result.Fault is { } fault
            ? result with { Fault = fault with { Detail = Tokonomy.Runtime.Ledger.Redactor.Redact(fault.Detail) } }
            : result;
    }

    private async Task<RunResult> RunCoreAsync(
        Brief brief, string workdir, string? model, TimeSpan timeout, List<(string Relative, string? Original)> restore, CancellationToken ct)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompt"] = brief.Prompt,
            ["model"] = model ?? "",
            ["workdir"] = workdir,
            ["proxy_url"] = session.ProxyUrl,
            ["proxy_root"] = session.ProxyRoot,
            ["proxy_token"] = session.ProxyToken,
        };

        {
            var grant = definition.Permissions.AllowAllCommands ? ShellGrant.AnyCommand : session.ShellGrant;
            AgySettingsFile? agy = null;
            var briefText = brief.Body;
            if (IsAgy(definition))
            {
                // Written next to the logs, outside the worktree: the worker must not be able to read or rewrite the
                // rules that confine it.
                Directory.CreateDirectory(session.LogDirectory);
                agy = AgySettings.Build(workdir, definition.Permissions, session.GateCommands, restrictions: session.Restrictions?.Invoke());
                var settingsPath = Path.Combine(session.LogDirectory, $"{brief.Label}.agy-settings.json");
                await File.WriteAllTextAsync(settingsPath, agy.Json, new UTF8Encoding(false), ct);
                vars["settings_file"] = settingsPath;

                // The first refused call ends the whole headless session, so the worker has to know the rules up front.
                briefText = brief.Body.TrimEnd() + "\n\n" + AgyBriefSection(agy, grant);
            }

            // Through the guard: a brief or config file that is a link would make Tokonomy write outside the worktree.
            var existing = WorktreeGuard.ReadAllText(workdir, definition.BriefFile);
            restore.Add((definition.BriefFile, existing));
            var body = existing is null ? briefText : existing.TrimEnd() + "\n\n" + BriefMarker + "\n\n" + briefText;
            WorktreeGuard.WriteAllText(workdir, definition.BriefFile, body);

            foreach (var (relative, template) in definition.Files)
            {
                restore.Add((relative, WorktreeGuard.ReadAllText(workdir, relative)));
                WorktreeGuard.WriteAllText(workdir, relative, ShellAccess.ApplyToFile(definition, relative, Substitute(template, vars), grant));
            }

            var args = ShellAccess.Apply(definition, BuildArgs(definition, vars, model), grant);

            var proxy = ProxySettings.Current.ForProvider(ProxyIdOf(definition));
            var env = EnvironmentBuilder.ForWorker(
                session.ExtraAllowedEnv,
                definition.Env.ToDictionary(kv => kv.Key, kv => Substitute(kv.Value, vars), StringComparer.Ordinal),
                null,
                proxy);

            Directory.CreateDirectory(session.LogDirectory);
            var stdout = Path.Combine(session.LogDirectory, $"{brief.Label}.stdout.log");
            var stderr = Path.Combine(session.LogDirectory, $"{brief.Label}.stderr.log");
            var outcome = await runner.RunAsync(new ProcessSpec
            {
                Args = args,
                WorkingDirectory = workdir,
                Environment = env,
                ReplaceEnvironment = true,
                Timeout = timeout,
                Proxy = proxy,
                StdoutFile = stdout,
                StderrFile = stderr,
            }, ct);

            var stdoutText = File.Exists(stdout) ? await File.ReadAllTextAsync(stdout, CancellationToken.None) : outcome.Stdout;

            if (agy is not null)
            {
                var report = AgyRunParser.Parse(stdoutText, model);
                var fault = AgyRunInterpreter.Evaluate(
                    report, outcome.ExitCode, outcome.Stderr, model, definition.VerifyModel, workdir, agy.AllowedCommands, agy.DeniedPaths,
                    allowAllCommands: grant.All || (grant.Commands.Count > 0 && ShellAccess.Enforcement(definition) == ShellEnforcement.UnitWideWithAudit));
                // Antigravity cannot refuse a single command, so the approved list is checked after the attempt.
                if (fault is null)
                {
                    var unapproved = ShellAccess.Violations(definition, grant, report.Tools.Where(t => t.IsCommand && t.Target is not null).Select(t => t.Target!));
                    if (unapproved.Count > 0)
                    {
                        fault = new(WorkerFaultKind.PermissionDenied, "ran command(s) that were not approved for this unit: " + string.Join(" | ", unapproved.Select(c => c.Length > 160 ? c[..160] : c))
                            + "; approved: " + string.Join(" | ", grant.Commands));
                    }
                }
                if (outcome.TimedOut && fault is { Kind: not WorkerFaultKind.ModelMismatch and not WorkerFaultKind.PermissionDenied })
                {
                    // A killed process has no result by definition; the supervisor handles the timeout itself.
                    fault = null;
                }

                return new RunResult
                {
                    ExitCode = outcome.ExitCode,
                    Duration = outcome.Duration,
                    TimedOut = outcome.TimedOut,
                    StdoutPath = stdout,
                    StderrPath = stderr,
                    Usage = report.Usage,
                    Fault = fault,
                    ReportedModel = report.Model,
                    Audit = new ExecutorAudit
                    {
                        ConversationId = report.ConversationId,
                        Status = report.Status,
                        Commands = report.Tools.Where(t => t.IsCommand && t.Target is not null).Select(t => t.Target!).ToList(),
                        Denied = report.DeniedActions
                            .Concat(report.Tools.Where(t => t.Errored).Select(t => $"{t.Name}: {t.Target}"))
                            .ToList(),
                        Notes = agy.Notes,
                    },
                };
            }

            return new RunResult
            {
                ExitCode = outcome.ExitCode,
                Duration = outcome.Duration,
                TimedOut = outcome.TimedOut,
                StdoutPath = stdout,
                StderrPath = stderr,
                Usage = ReadUsage(definition.UsageFormat, stdoutText, model),
                // A non-zero exit is a failed run even if some files changed; never let the gates talk it into an accept.
                Fault = !outcome.TimedOut && outcome.ExitCode != 0
                    ? new WorkerFault(WorkerFaultKind.ProcessFailed, $"exit code {outcome.ExitCode}: {CommandGate.Tail(outcome.Stderr, 400)}")
                    : null,
            };
        }
    }

    /// <summary>Restores files Tokonomy wrote for one attempt. Returns signs of tampering (empty when none).</summary>
    internal static List<string> Restore(string workdir, List<(string Relative, string? Original)> restore)
    {
        var problems = new List<string>();
        foreach (var (relative, original) in Enumerable.Reverse(restore))
        {
            try
            {
                if (WorktreeGuard.IsLink(WorktreeGuard.ResolveInside(workdir, relative)))
                {
                    problems.Add($"{relative} was replaced by a link");
                    WorktreeGuard.DeleteFile(workdir, relative);
                }

                if (original is null)
                {
                    WorktreeGuard.DeleteFile(workdir, relative);
                }
                else
                {
                    WorktreeGuard.WriteAllText(workdir, relative, original);
                }
            }
            catch (IOException ex)
            {
                problems.Add($"{relative} could not be restored safely: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                problems.Add($"{relative} could not be restored safely: {ex.Message}");
            }
        }

        restore.Clear();
        return problems;
    }

    internal static string AgyBriefSection(AgySettingsFile settings, ShellGrant grant)
    {
        var sb = new StringBuilder();
        if (!grant.All && grant.Commands.Count > 0)
        {
            sb.AppendLine("## Tool permissions");
            sb.AppendLine("The user approved exactly these shell commands for this unit. Run them as written, from inside this repository, and nothing else:");
            foreach (var command in grant.Commands)
            {
                sb.AppendLine($"  - `{command}`");
            }

            sb.AppendLine("- Edit files with your file tools, inside this repository only. Never read credential or login files.");
            sb.AppendLine("- Any other command is a violation: the attempt is rejected. If you need another command, say so in your report instead of running it.");
            return sb.ToString();
        }

        if (grant.All)
        {
            sb.AppendLine("## Tool permissions");
            sb.AppendLine("You may run shell commands (build, test, restore, generate code), always from inside this repository.");
            sb.AppendLine("- Edit files with your file tools, inside this repository only. Do not read or change anything outside it.");
            sb.AppendLine("- Never read credential or login files, and never print secrets.");
            sb.AppendLine("- Do not run git commands that change history or the remote, and do not delete anything outside this repository.");
            return sb.ToString();
        }

        sb.AppendLine("## Tool permissions (enforced)");
        sb.AppendLine("This session is headless and cannot ask for permission. The FIRST tool call that is not allowed ends the session");
        sb.AppendLine("immediately with nothing saved, so do not try anything that is not listed here.");
        sb.AppendLine("- Read and edit files with your file tools, inside this repository only. Do not touch paths outside it.");
        sb.AppendLine("- Do not use the shell to read, list, search or edit files (no cat, ls, Get-ChildItem, python -c, sed, ...).");
        if (settings.AllowedCommands.Count == 0)
        {
            sb.AppendLine("- You may run NO shell commands at all.");
        }
        else
        {
            sb.AppendLine("- The ONLY shell commands you may run, exactly as written, one per call, no extra arguments, no chaining:");
            foreach (var command in settings.AllowedCommands)
            {
                sb.AppendLine($"  - `{command}`");
            }
        }

        sb.AppendLine("- No network access, no git commands, no installing packages.");
        return sb.ToString();
    }

    /// <summary>The connection an agent CLI talks to, for the proxy scope: claude, codex, antigravity, otherwise a tool like OpenCode.</summary>
    internal static string ProxyIdOf(ExecutorDefinition definition) =>
        Path.GetFileNameWithoutExtension(definition.Cmd.FirstOrDefault() ?? "").ToLowerInvariant() switch
        {
            "claude" => "claude",
            "codex" => "codex",
            "agy" => "antigravity",
            _ => ProxySettings.OtherId,
        };

    public static bool IsAgy(ExecutorDefinition definition) =>
        string.Equals(definition.UsageFormat, "agy-json", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<WorkerUsage> ReadUsage(string? format, string stdoutText, string? model)
    {
        if (string.IsNullOrWhiteSpace(format) || string.IsNullOrEmpty(stdoutText))
        {
            return [];
        }

        return format.ToLowerInvariant() switch
        {
            "claude-json" => ClaudeUsageParser.Parse(stdoutText),
            "agy-json" => AgyUsageParser.Parse(stdoutText, model),
            "codex-json" => CodexRun.Parse(stdoutText).ToUsage(model),
            _ => [],
        };
    }

    internal static List<string> BuildArgs(ExecutorDefinition definition, IReadOnlyDictionary<string, string> vars, string? model)
    {
        var args = definition.Cmd.Select(a => Substitute(a, vars)).ToList();
        if (!string.IsNullOrEmpty(model) && definition.Capabilities.ModelOverride)
        {
            args.AddRange(definition.ModelArgs.Select(a => Substitute(a, vars)));
        }

        return args;
    }

    /// <summary>Replaces <c>{name}</c> placeholders for known names only; anything else (e.g. <c>{env:X}</c>) is left as is.</summary>
    internal static string Substitute(string template, IReadOnlyDictionary<string, string> vars)
    {
        var sb = new StringBuilder(template);
        foreach (var (key, value) in vars)
        {
            sb.Replace("{" + key + "}", value);
        }

        return sb.ToString();
    }
}
