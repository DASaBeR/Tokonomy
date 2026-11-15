using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Bench;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Cli.Commands;

public sealed class BenchSettings : CommonSettings
{
    [CommandArgument(0, "<TASKSET>")]
    [Description("Path to a tasks.yaml taskset.")]
    public string Taskset { get; init; } = "";

    [CommandOption("--strategies <LIST>")]
    [Description("Comma-separated strategies: direct, 1, 1b, 2, 3, 4, 5. direct = the strong CLI on the raw task, no Tokonomy brief.")]
    [DefaultValue("1,1b,2")]
    public string Strategies { get; init; } = "1,1b,2";

    [CommandOption("--repeat <N>")]
    [Description("Trials per task and strategy (model output is non-deterministic).")]
    [DefaultValue(3)]
    public int Repeat { get; init; } = 3;

    [CommandOption("--task <ID>")]
    [Description("Only run these task ids (repeatable).")]
    public string[]? Tasks { get; init; }

    [CommandOption("--worker <MODEL>")]
    [Description("Override models.worker for this run (needs a price in prices.yaml).")]
    public string? Worker { get; init; }

    [CommandOption("--strong <MODEL>")]
    [Description("Override models.architect, the strong model, for this run (needs a price in prices.yaml).")]
    public string? Strong { get; init; }

    [CommandOption("--max-usd <USD>")]
    [Description("Stop starting trials once this much has been spent in total. Each trial is also capped at what remains.")]
    public decimal? MaxUsd { get; init; }

    [CommandOption("--share-plans")]
    [Description("Paired comparison: strategies that use an architect plan run the same plan per task and repeat (planning cost is charged to each).")]
    public bool SharePlans { get; init; }

    [CommandOption("--judge")]
    [Description("After each trial, an independent architect call judges the final diff against the task (not charged to the trial).")]
    public bool Judge { get; init; }

    [CommandOption("--seed <N>")]
    [Description("Shuffle the trial order with this seed (recorded in the manifest) so arms are interleaved instead of run in blocks.")]
    public int? Seed { get; init; }

    [CommandOption("--max-trials <N>")]
    [Description("Start at most this many trials (a run-count cap: the budget on a subscription is a number of runs).")]
    public int? MaxTrials { get; init; }

    [CommandOption("--pause-seconds <N>")]
    [Description("Wait this long between trials to stay under subscription rate limits.")]
    [DefaultValue(0)]
    public int PauseSeconds { get; init; }

    [CommandOption("--stop-after-errors <N>")]
    [Description("Stop after this many trials in a row ended in an environment error such as a usage limit (0 disables).")]
    [DefaultValue(3)]
    public int StopAfterErrors { get; init; } = 3;

    [CommandOption("--max-judge-usd <USD>")]
    [Description("Cap on the money API judges may spend in total; judging then reports Unavailable. Required when a judge is an api judge.")]
    public decimal? MaxJudgeUsd { get; init; }

    [CommandOption("--dry-run")]
    [Description("Show the trial matrix and budget ceiling without running anything.")]
    public bool DryRun { get; init; }
}

public sealed partial class BenchCommand(IAnsiConsole console, IProcessRunner runner, Services services) : AsyncCommand<BenchSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BenchSettings settings, CancellationToken cancellationToken)
    {
        Taskset taskset;
        List<Strategy> strategies;
        try
        {
            taskset = Taskset.Load(settings.Taskset);
            strategies = settings.Strategies.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Strategy.Parse).ToList();
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or ArgumentException)
        {
            Output.Error(console, ex.Message);
            return ExitCodes.Usage;
        }

        if (settings.Repeat < 1)
        {
            Output.Error(console, "--repeat must be at least 1.");
            return ExitCodes.Usage;
        }

        if (settings.MaxTrials is < 1 || settings.PauseSeconds < 0 || settings.StopAfterErrors < 0 || settings.MaxJudgeUsd is <= 0)
        {
            Output.Error(console, "--max-trials must be at least 1; --pause-seconds, --stop-after-errors and --max-judge-usd must not be negative (--max-judge-usd positive).");
            return ExitCodes.Usage;
        }

        if (settings.MaxUsd is <= 0)
        {
            Output.Error(console, "--max-usd must be positive.");
            return ExitCodes.Usage;
        }

        var configDir = settings.Config ?? taskset.ConfigDirectory;
        var config = ConfigLoader.Load(configDir);
        if (config.HasErrors)
        {
            Output.Issues(console, config.Issues);
            return ExitCodes.ConfigError;
        }

        var models = config.Policy.Models;
        if (!string.IsNullOrWhiteSpace(settings.Worker))
        {
            models.Worker = settings.Worker;
        }

        if (!string.IsNullOrWhiteSpace(settings.Strong))
        {
            models.Architect = settings.Strong;
        }

        if (string.IsNullOrWhiteSpace(models.Architect) || (strategies.Any(s => !s.WorkerIsStrong) && string.IsNullOrWhiteSpace(models.Worker)))
        {
            Output.Error(console, $"Set models.architect and models.worker in {Path.Combine(config.ConfigDirectory, TokonomyConfig.PolicyFile)}.");
            return ExitCodes.ConfigError;
        }

        var prices = Services.LoadPrices(config);

        // A model without a price makes every call cost $0 in the ledger, which silently disables the spending caps.
        // Only models whose cost nobody else reports need one: Claude Code reports its own cost, and a claude-cli
        // architect does too.
        static bool NeedsPrice(ExecutorDefinition e) => !string.Equals(e.UsageFormat, "claude-json", StringComparison.OrdinalIgnoreCase);
        var used = new List<string>();
        var architectMetered = config.Policy.ArchitectProvider.Kind != ArchitectProviderConfig.ClaudeCliKind;
        var strongExecutor = config.Executors.Executors[config.Policy.Executors.For(strongModelDoesTheWork: true)];
        var cheapExecutor = config.Executors.Executors[config.Policy.Executors.Default];
        if (strategies.Any(s => (s.WorkerIsStrong && NeedsPrice(strongExecutor)) || (s.UsesArchitectPlan && architectMetered)))
        {
            used.Add(models.Architect);
        }

        if (strategies.Any(s => !s.WorkerIsStrong) && NeedsPrice(cheapExecutor))
        {
            used.Add(models.Worker);
        }

        var unpriced = used.Distinct(StringComparer.OrdinalIgnoreCase).Where(m => !prices.Has(m)).ToList();
        if (unpriced.Count > 0)
        {
            Output.Error(console, $"No price in prices.yaml for: {string.Join(", ", unpriced)}. " +
                                  "Without a price the spending caps cannot be enforced; add an entry (USD per million tokens).");
            return ExitCodes.ConfigError;
        }

        var apiJudges = taskset.Judges.Where(j => j.Provider == "api").ToList();
        var unpricedJudges = apiJudges.Where(j => !prices.Has(j.Model)).Select(j => j.Model).ToList();
        if (unpricedJudges.Count > 0)
        {
            Output.Error(console, $"No price in prices.yaml for judge model(s): {string.Join(", ", unpricedJudges)}. Without it the judge spend cap cannot work; add an entry (USD per million tokens).");
            return ExitCodes.ConfigError;
        }

        if (apiJudges.Count > 0 && settings.MaxJudgeUsd is null)
        {
            Output.Error(console, "The taskset has an api judge: pass --max-judge-usd to cap what judging may spend.");
            return ExitCodes.Usage;
        }

        var tasks = taskset.Tasks.Where(t => settings.Tasks is null || settings.Tasks.Contains(t.Id)).ToList();
        var trials = Math.Min(tasks.Count * strategies.Count * settings.Repeat, settings.MaxTrials ?? int.MaxValue);
        var ceiling = trials * config.Policy.Budget.MaxUsdPerTask;
        if (settings.MaxUsd is { } totalCap)
        {
            ceiling = Math.Min(ceiling, totalCap);
        }

        console.MarkupLine($"{tasks.Count} task(s) × {strategies.Count} strategies × {settings.Repeat} repeat(s) = [bold]{trials}[/] trials");
        var totalCapText = settings.MaxUsd is { } c ? ", --max-usd $" + c.ToString("0.00", CultureInfo.InvariantCulture) + " in total" : "";
        console.MarkupLine(string.Create(CultureInfo.InvariantCulture,
            $"Budget ceiling: ${ceiling:0.00} (max_usd_per_task ${config.Policy.Budget.MaxUsdPerTask:0.00} per trial{totalCapText})"));
        if (settings.DryRun)
        {
            foreach (var j in taskset.Judges)
            {
                console.MarkupLine($"  judge {Markup.Escape(j.Name)}: {j.Provider} / {Markup.Escape(j.Model)}");
            }

            foreach (var s in strategies)
            {
                console.MarkupLine($"  {s.Id}: {Markup.Escape(s.Description)}");
            }

            return ExitCodes.Ok;
        }

        var key = services.GetEnvironmentVariable(config.Policy.Provider.ApiKeyEnv);
        IProvider? provider = strategies.Any(s => s.UsesArchitectPlan) ? services.ProviderFactory(config, prices) : null;

        using var ledger = new SqliteLedger(config.LedgerPath);
        List<BenchJudge> judges;
        try
        {
            judges = taskset.Judges.Select(j => new BenchJudge(j.Name, services.CreateJudgeProvider(j, prices), j.Model, Metered: j.Provider == "api")).ToList();
        }
        catch (Tokonomy.Llm.Providers.MissingApiKeyException ex)
        {
            Output.Error(console, ex.Message);
            return ExitCodes.ConfigError;
        }

        var bench = new BenchRunner(runner, ledger, new Supervisor(), provider, services.ExecutorFactory, judges: judges);
        var benchId = await bench.RunAsync(taskset, config, new BenchOptions
        {
            Strategies = strategies,
            Repeat = settings.Repeat,
            TaskFilter = settings.Tasks,
            MaxUsd = settings.MaxUsd,
            SharePlans = settings.SharePlans,
            Judge = settings.Judge || judges.Count > 0,
            Seed = settings.Seed,
            MaxTrials = settings.MaxTrials,
            Pause = TimeSpan.FromSeconds(settings.PauseSeconds),
            StopAfterConsecutiveInfraFailures = settings.StopAfterErrors,
            MaxJudgeUsd = settings.MaxJudgeUsd,
            Preregistration = taskset.Preregistration,
            Environment = await DescribeEnvironmentAsync(config, cancellationToken),
        }, prices, key, new ConsoleBenchObserver(console), cancellationToken);

        var results = ledger.ListBenchResults(benchId);
        var markdown = BenchReport.Markdown(benchId, results, strategies.Select(s => s.Id).ToList());
        var reportPath = Path.Combine(config.ConfigDirectory, "runs", benchId + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, markdown, cancellationToken);
        // Raw per-trial data (with the ledger-derived statistics) for re-analysis.
        await File.WriteAllTextAsync(Path.ChangeExtension(reportPath, ".json"),
            System.Text.Json.JsonSerializer.Serialize(new { bench_id = benchId, results }, RunArtifacts.JsonOptions), cancellationToken);

        if (settings.Json)
        {
            Output.Json(console, new { bench_id = benchId, report = reportPath, strategies = BenchReport.Summarize(results), results });
            return ExitCodes.Ok;
        }

        console.WriteLine();
        console.Write(new Text(markdown));
        console.MarkupLine($"Report saved to {Markup.Escape(reportPath)}");
        return ExitCodes.Ok;
    }
}

public sealed partial class BenchCommand
{
    /// <summary>Versions of the tools a trial depends on, for the manifest. A tool that cannot be queried is recorded as such.</summary>
    private async Task<Dictionary<string, string>> DescribeEnvironmentAsync(TokonomyConfig config, CancellationToken ct)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tokonomy"] = typeof(BenchCommand).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "dev",
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        };
        var executors = new[] { config.Policy.Executors.Default, config.Policy.Executors.Strong }.OfType<string>().Distinct();
        foreach (var name in executors)
        {
            if (config.Executors.Executors.TryGetValue(name, out var e) && e.VersionCmd.Count > 0)
            {
                env[$"executor:{name}"] = await VersionAsync(e.VersionCmd, ct);
            }
        }

        if (config.Policy.ArchitectProvider.Kind != ArchitectProviderConfig.ApiKind)
        {
            env["architect_cli"] = await VersionAsync([config.Policy.ArchitectProvider.CommandOrDefault, "--version"], ct);
        }

        var root = Path.GetDirectoryName(config.ConfigDirectory)!;
        var git = new Tokonomy.Runtime.Git.GitCli(runner);
        env["git_head"] = (await git.RunAsync(root, ["rev-parse", "HEAD"], ct, allowFailure: true)).Trim();
        var dirty = await git.RunAsync(root, ["status", "--porcelain"], ct, allowFailure: true);
        env["git_dirty_files"] = dirty.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ToString(CultureInfo.InvariantCulture);
        var diff = await git.RunAsync(root, ["diff", "HEAD"], ct, allowFailure: true);
        env["git_diff_sha"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(diff)))[..12];
        return env;
    }

    private async Task<string> VersionAsync(IReadOnlyList<string> cmd, CancellationToken ct)
    {
        try
        {
            var result = await runner.RunAsync(new ProcessSpec { Args = cmd, WorkingDirectory = Directory.GetCurrentDirectory(), Timeout = TimeSpan.FromSeconds(30) }, ct);
            return (result.Stdout + result.Stderr).Trim().Split('\n')[0].Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "unavailable: " + ex.Message;
        }
    }
}

internal sealed class ConsoleBenchObserver(IAnsiConsole console) : IBenchObserver
{
    public void TrialStarted(BenchTask task, Strategy strategy, int repeat, int index, int total) =>
        console.MarkupLine($"[grey][[{index}/{total}]][/] {Markup.Escape(task.Id)}  strategy {strategy.Id}  repeat {repeat}");

    public void Stopped(string reason, int skippedTrials) =>
        console.MarkupLine($"[yellow]Stopped: {Markup.Escape(reason)}; {skippedTrials} trial(s) not run.[/]");

    public void TrialFinished(BenchResultRecord result) =>
        console.MarkupLine(string.Create(CultureInfo.InvariantCulture,
            $"  {(result.Accepted ? "[green]accepted[/]" : "[red]rejected[/]")}  run {result.RunStatus}  ${result.Usd:0.0000}") +
            (result.Note is null ? "" : $"  [grey]{Markup.Escape(result.Note)}[/]"));
}
