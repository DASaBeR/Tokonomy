using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Config;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Gates.Isolation;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Cli.Commands;

public sealed class DoctorSettings : CommonSettings
{
    [CommandOption("--remove-leftover-worktrees")]
    [System.ComponentModel.Description("Safely remove tokonomy worktrees left by interrupted runs (links inside are removed as links first; worktrees of running runs are skipped).")]
    public bool RemoveLeftoverWorktrees { get; init; }

    [CommandOption("--remove-leftover-gate-environments")]
    [System.ComponentModel.Description("Stop Windows Sandbox environments left by crashed runs and delete their folders (including ones kept by cleanup_policy: keep-on-failure). Sandboxes Tokonomy did not start are never touched.")]
    public bool RemoveLeftoverGateEnvironments { get; init; }
}

public sealed partial class DoctorCommand(IAnsiConsole console, IProcessRunner runner) : AsyncCommand<DoctorSettings>
{
    private enum Level
    {
        Ok,
        Warn,
        Fail,
    }

    private sealed record Check(string Area, string Name, Level Level, string Detail);

    public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings settings, CancellationToken cancellationToken)
    {
        var checks = new List<Check>();
        var cwd = Directory.GetCurrentDirectory();

        checks.Add(await VersionCheck("tools", "git", ["git", "--version"], cwd, null, required: true, cancellationToken));

        var dir = settings.Config ?? ConfigLoader.FindConfigDirectory(cwd);
        if (dir is null)
        {
            checks.Add(new("config", TokonomyConfig.DirectoryName, Level.Fail, "not found; run `tokonomy init`"));
            return Render(checks, settings.Json);
        }

        var config = ConfigLoader.Load(dir);
        foreach (var issue in config.Issues)
        {
            checks.Add(new("config", Path.GetFileName(issue.File), issue.Severity == Severity.Error ? Level.Fail : Level.Warn,
                (issue.Line is { } l ? $"line {l}: " : "") + issue.Message));
        }

        if (config.HasErrors)
        {
            return Render(checks, settings.Json);
        }

        checks.Add(new("config", TokonomyConfig.DirectoryName, Level.Ok, config.ConfigDirectory));
        var repo = config.RepoRoot;
        var policy = config.Policy;

        // Executor.
        var execName = policy.Executors.Default;
        var exec = config.Executors.Executors[execName];
        if (exec.VersionCmd.Count > 0)
        {
            var check = await VersionCheck("executor", execName, exec.VersionCmd, repo, exec.TestedVersion, required: true, cancellationToken);
            checks.Add(check);
        }
        else
        {
            var found = ExecutableResolver.TryResolve(exec.Cmd[0], repo);
            checks.Add(new("executor", execName, found is null ? Level.Fail : Level.Ok, found ?? $"'{exec.Cmd[0]}' not on PATH"));
        }

        if (!exec.Capabilities.Headless)
        {
            checks.Add(new("executor", "headless", Level.Fail, $"{execName} does not declare headless support"));
        }

        if (Tokonomy.Runtime.Executors.ConfigExecutor.IsAgy(exec))
        {
            checks.AddRange(await AgyChecks(exec, policy, repo, cancellationToken));
        }

        if (!string.IsNullOrEmpty(policy.Models.Worker) && !exec.Capabilities.ModelOverride)
        {
            checks.Add(new("executor", "model override", Level.Warn,
                $"{execName} cannot take a model override; models.worker will be ignored"));
        }

        if (!exec.Capabilities.ProxyRoutable && !exec.Capabilities.ReportsTokens)
        {
            checks.Add(new("executor", "metering", Level.Warn, "worker cost will be recorded as estimated"));
        }

        // Architect provider.
        var cliArchitect = policy.ArchitectProvider.Kind == ArchitectProviderConfig.ClaudeCliKind;
        if (cliArchitect)
        {
            var claude = ExecutableResolver.TryResolve(policy.ArchitectProvider.Command, repo);
            checks.Add(new("architect", policy.ArchitectProvider.Command, claude is null ? Level.Fail : Level.Ok,
                claude is null ? $"'{policy.ArchitectProvider.Command}' not on PATH" : "Claude Code CLI (cost recorded as estimated)"));
        }

        // Provider key: report presence only, never the value. Needed for an API architect or a metered worker.
        var keySet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(policy.Provider.ApiKeyEnv));
        var keyNeeded = !cliArchitect || exec.Capabilities.ProxyRoutable;
        checks.Add(new("provider", policy.Provider.ApiKeyEnv, keySet || !keyNeeded ? Level.Ok : Level.Fail,
            keySet ? "set" : keyNeeded ? "not set" : "not set (not needed: architect and worker use CLI logins)"));

        // Prices.
        var prices = PriceTable.Load(Path.Combine(config.ConfigDirectory, TokonomyConfig.PricesFile));
        foreach (var (role, model) in new[] { ("architect", policy.Models.Architect), ("worker", policy.Models.Worker) })
        {
            if (!string.IsNullOrEmpty(model))
            {
                checks.Add(new("prices", $"{role}: {model}", prices.Has(model) ? Level.Ok : Level.Warn,
                    prices.Has(model) ? "priced" : "no price in prices.yaml; cost will be estimated"));
            }
        }

        // Gate tools.
        foreach (var gate in policy.Gates.Where(g => g.Type == GateConfig.CommandType))
        {
            var found = ExecutableResolver.TryResolve(gate.Cmd[0], repo);
            checks.Add(new("gates", gate.DisplayName, found is null ? Level.Fail : Level.Ok, found ?? $"'{gate.Cmd[0]}' not on PATH"));
        }

        checks.AddRange(await WorktreeChecks(policy, repo, config.LedgerPath, settings.RemoveLeftoverWorktrees, cancellationToken));
        checks.AddRange(await GateExecutionChecks(policy, repo, settings.RemoveLeftoverGateEnvironments, cancellationToken));

        // Ledger.
        try
        {
            using var ledger = new SqliteLedger(config.LedgerPath);
            checks.Add(new("ledger", TokonomyConfig.LedgerFile, Level.Ok, $"schema v{ledger.SchemaVersion}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            checks.Add(new("ledger", TokonomyConfig.LedgerFile, Level.Fail, ex.Message));
        }

        if (OperatingSystem.IsWindows())
        {
            checks.Add(await LongPathsCheck(repo, cancellationToken));
        }

        return Render(checks, settings.Json);
    }

    /// <summary>Checks specific to the Antigravity worker: the requested model exists, and write isolation is in place.</summary>
    private async Task<List<Check>> AgyChecks(ExecutorDefinition exec, Policy policy, string repo, CancellationToken ct)
    {
        var checks = new List<Check>();
        var model = policy.Models.Worker;
        if (!string.IsNullOrWhiteSpace(model) && ExecutableResolver.TryResolve(exec.Cmd[0], repo) is not null)
        {
            try
            {
                var outcome = await runner.RunAsync(new ProcessSpec { Args = [exec.Cmd[0], "models"], WorkingDirectory = repo, Timeout = TimeSpan.FromSeconds(60) }, ct);
                var offered = outcome.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => l.Split('\t')[0].Trim())
                    .Where(id => id.Length > 0 && !id.Contains(' '))
                    .ToList();
                if (!outcome.Succeeded || offered.Count == 0)
                {
                    checks.Add(new("worker model", model, Level.Warn, $"could not list models (exit {outcome.ExitCode}); the model is verified per run instead"));
                }
                else
                {
                    var found = offered.Contains(model, StringComparer.OrdinalIgnoreCase);
                    checks.Add(new("worker model", model, found ? Level.Ok : Level.Fail,
                        found ? "offered by `agy models`" : $"not offered by `agy models` ({string.Join(", ", offered)})"));
                }
            }
            catch (Exception ex) when (ex is ExecutableNotFoundException or IOException or InvalidOperationException)
            {
                checks.Add(new("worker model", model, Level.Warn, ex.Message));
            }
        }

        if (exec.Permissions.AllowGateCommands || exec.Permissions.Commands.Count > 0)
        {
            checks.Add(new("isolation", "worker commands", Level.Warn,
                "the worker may run shell commands; a command that runs repository code (tests, builds) runs code the worker wrote, with your privileges"));
        }

        return checks;
    }

    /// <summary>Where gates run, whether that works on this machine, and environments left behind by crashed runs.</summary>
    private async Task<List<Check>> GateExecutionChecks(Policy policy, string repo, bool remove, CancellationToken ct)
    {
        var checks = new List<Check>();
        var commands = GateExecutionFactory.Commands(policy).ToList();
        if (policy.GateExecution is not { } gx)
        {
            checks.Add(new("gate isolation", "provider", commands.Count > 0 ? Level.Fail : Level.Ok,
                commands.Count > 0 ? GateExecutionFactory.MissingSection : "not configured (no gate or prepare commands)"));
            return checks;
        }

        IGateExecutionProvider provider;
        try
        {
            provider = GateExecutionFactory.Create(gx, runner, repo, commands, [policy.Provider.ApiKeyEnv], policy.Isolation.WorktreeRoot);
        }
        catch (GateIsolationUnavailableException ex)
        {
            checks.Add(new("gate isolation", gx.Provider, Level.Fail, ex.Message));
            return checks;
        }

        var status = await provider.CheckAsync(ct);
        checks.Add(new("gate isolation", provider.Name,
            !status.Available ? Level.Fail : provider.IsIsolated ? Level.Ok : Level.Warn,
            provider.IsIsolated ? status.Detail : "NOT ISOLATED: gates and prepare steps run code the worker wrote on this machine with your privileges"));
        if (gx.NetworkPolicy == GateExecutionConfig.NetworkFull)
        {
            checks.Add(new("gate isolation", "network", Level.Warn, "network_policy: full lets code under test reach the internet, your LAN and this machine's network services"));
        }

        if (provider is not WindowsSandboxProvider sandbox)
        {
            return checks;
        }

        foreach (var toolchain in sandbox.Toolchains)
        {
            checks.Add(new("gate isolation", "toolchain", Level.Ok, $"{toolchain.HostPath} -> {toolchain.SandboxPath} (read-only)"));
        }

        if (remove)
        {
            foreach (var note in await sandbox.RecoverAsync(includeKept: true, ct))
            {
                checks.Add(new("gate isolation", "leftover environment", note.EndsWith(": removed", StringComparison.Ordinal) ? Level.Ok : Level.Warn, note));
            }
        }
        else
        {
            var store = new GateEnvStore(GateEnvStore.DefaultRoot()!);
            var leftovers = store.List().Where(GateEnvStore.IsOrphaned).ToList();
            if (leftovers.Count > 0)
            {
                checks.Add(new("gate isolation", "leftover environments", Level.Warn,
                    $"{leftovers.Count} environment(s) from earlier runs in {store.Root} (e.g. {leftovers[0].EnvId}); remove them with `tokonomy doctor --remove-leftover-gate-environments`"));
            }
        }

        return checks;
    }

    /// <summary>The effective worktree root, whether it is safe, and worktrees left behind by earlier runs.</summary>
    private async Task<List<Check>> WorktreeChecks(Policy policy, string repo, string ledgerPath, bool remove, CancellationToken ct)
    {
        var checks = new List<Check>();
        var root = WorktreeRoot.Effective(policy.Isolation.WorktreeRoot, repo);
        var source = string.IsNullOrWhiteSpace(policy.Isolation.WorktreeRoot) ? "default" : "isolation.worktree_root";
        var problems = WorktreeRoot.Validate(root, repo);
        checks.Add(new("isolation", "worktree root", problems.Count == 0 ? Level.Ok : Level.Fail,
            problems.Count == 0 ? $"{root} ({source})" : $"{root} ({source}) is unsafe: {string.Join("; ", problems)}"));

        // Worktrees from runs that were interrupted (or from versions that used the temp directory) stay registered.
        // They are reported, never moved or deleted automatically.
        string list;
        try
        {
            list = await new GitCli(runner).RunAsync(repo, ["worktree", "list", "--porcelain"], ct);
        }
        catch (GitException)
        {
            return checks;
        }

        var stale = new List<(string Path, bool InTemp)>();
        string? path = null;
        foreach (var line in list.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = Path.GetFullPath(line["worktree ".Length..]);
            }
            else if (path is not null && line.StartsWith("branch refs/heads/tokonomy/", StringComparison.Ordinal))
            {
                stale.Add((path, WorktreeRoot.TempDirectories().Any(t => WorktreeRoot.Overlaps(path, t))));
            }
        }

        if (stale.Count == 0)
        {
            return checks;
        }

        var inTemp = stale.Count(x => x.InTemp);
        if (!remove)
        {
            checks.Add(new("isolation", "leftover worktrees", inTemp > 0 ? Level.Warn : Level.Ok,
                $"{stale.Count} tokonomy worktree(s) still registered ({inTemp} in temp), e.g. {stale[0].Path}. " +
                "Remove the ones from finished runs with `tokonomy doctor --remove-leftover-worktrees` (do not use plain " +
                "`git worktree remove --force`: on Windows it follows junctions and deletes what they point to)."));
            return checks;
        }

        using var ledger = new SqliteLedger(ledgerPath);
        var git = new GitCli(runner);
        foreach (var (worktree, _) in stale)
        {
            var runId = Path.GetFileName(worktree.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (ledger.GetRun(runId) is { Status: Core.Model.RunStatus.Running })
            {
                checks.Add(new("isolation", worktree, Level.Warn, "run is still marked running; skipped"));
                continue;
            }

            var problem = await RunWorkspace.RemoveLeftoverAsync(git, repo, worktree, ct);
            checks.Add(new("isolation", worktree, problem is null ? Level.Ok : Level.Warn, problem ?? "removed"));
        }

        return checks;
    }

    private async Task<Check> VersionCheck(string area, string name, List<string> cmd, string cwd, string? tested, bool required, CancellationToken ct)
    {
        try
        {
            var outcome = await runner.RunAsync(new ProcessSpec { Args = cmd, WorkingDirectory = cwd, Timeout = TimeSpan.FromSeconds(20) }, ct);
            var text = (outcome.Stdout + outcome.Stderr).Trim();
            var version = VersionRegex().Match(text) is { Success: true } m ? m.Value : text.Split('\n')[0];
            if (!outcome.Succeeded)
            {
                return new(area, name, required ? Level.Fail : Level.Warn, $"exit {outcome.ExitCode}: {Truncate(text)}");
            }

            if (tested is not null && !string.Equals(version, tested, StringComparison.Ordinal))
            {
                return new(area, name, Level.Warn, $"{version} (adapter tested with {tested})");
            }

            return new(area, name, Level.Ok, tested is null && area == "executor" ? $"{version} (adapter version not pinned)" : version);
        }
        catch (ExecutableNotFoundException)
        {
            return new(area, name, required ? Level.Fail : Level.Warn, $"'{cmd[0]}' not on PATH");
        }
    }

    private async Task<Check> LongPathsCheck(string repo, CancellationToken ct)
    {
        try
        {
            var outcome = await runner.RunAsync(
                new ProcessSpec { Args = ["git", "config", "--get", "core.longpaths"], WorkingDirectory = repo, Timeout = TimeSpan.FromSeconds(10) }, ct);
            return outcome.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                ? new("tools", "git core.longpaths", Level.Ok, "true")
                : new("tools", "git core.longpaths", Level.Warn, "not enabled; worktrees with deep paths may fail (git config --global core.longpaths true)");
        }
        catch (ExecutableNotFoundException)
        {
            return new("tools", "git core.longpaths", Level.Warn, "git not found");
        }
    }

    private int Render(List<Check> checks, bool json)
    {
        var failed = checks.Any(c => c.Level == Level.Fail);
        if (json)
        {
            Output.Json(console, new { ok = !failed, checks });
            return failed ? ExitCodes.Failed : ExitCodes.Ok;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumns("", "Area", "Check", "Detail");
        foreach (var c in checks)
        {
            var mark = c.Level switch { Level.Ok => "[green]ok[/]", Level.Warn => "[yellow]warn[/]", _ => "[red]FAIL[/]" };
            table.AddRow(mark, Markup.Escape(c.Area), Markup.Escape(c.Name), Markup.Escape(c.Detail));
        }

        console.Write(table);
        console.MarkupLine(failed ? "[red]Some checks failed.[/]" : "[green]All required checks passed.[/]");
        return failed ? ExitCodes.Failed : ExitCodes.Ok;
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..117] + "...";

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?([-+][0-9A-Za-z.\-]+)?")]
    private static partial Regex VersionRegex();
}
