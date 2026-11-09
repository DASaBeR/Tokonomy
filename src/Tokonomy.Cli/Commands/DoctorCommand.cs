using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Config;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Cli.Commands;

public sealed partial class DoctorCommand(IAnsiConsole console, IProcessRunner runner) : AsyncCommand<CommonSettings>
{
    private enum Level
    {
        Ok,
        Warn,
        Fail,
    }

    private sealed record Check(string Area, string Name, Level Level, string Detail);

    public override async Task<int> ExecuteAsync(CommandContext context, CommonSettings settings, CancellationToken cancellationToken)
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
