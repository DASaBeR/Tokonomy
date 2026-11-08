using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Taskrail.Bench;
using Taskrail.Cli.Infrastructure;
using Taskrail.Core.Abstractions;
using Taskrail.Core.Model;
using Taskrail.Core.Supervision;
using Taskrail.Runtime.Config;
using Taskrail.Runtime.Ledger;
using Taskrail.Runtime.Orchestration;
using Taskrail.Runtime.Process;

namespace Taskrail.Cli.Commands;

public sealed class BenchSettings : CommonSettings
{
    [CommandArgument(0, "<TASKSET>")]
    [Description("Path to a tasks.yaml taskset.")]
    public string Taskset { get; init; } = "";

    [CommandOption("--strategies <LIST>")]
    [Description("Comma-separated strategies: 1, 1b, 2, 3, 4.")]
    [DefaultValue("1,1b,2")]
    public string Strategies { get; init; } = "1,1b,2";

    [CommandOption("--repeat <N>")]
    [Description("Trials per task and strategy (model output is non-deterministic).")]
    [DefaultValue(3)]
    public int Repeat { get; init; } = 3;

    [CommandOption("--task <ID>")]
    [Description("Only run these task ids (repeatable).")]
    public string[]? Tasks { get; init; }

    [CommandOption("--dry-run")]
    [Description("Show the trial matrix and budget ceiling without running anything.")]
    public bool DryRun { get; init; }
}

public sealed class BenchCommand(IAnsiConsole console, IProcessRunner runner, Services services) : AsyncCommand<BenchSettings>
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

        var configDir = settings.Config ?? taskset.ConfigDirectory;
        var config = ConfigLoader.Load(configDir);
        if (config.HasErrors)
        {
            Output.Issues(console, config.Issues);
            return ExitCodes.ConfigError;
        }

        var models = config.Policy.Models;
        if (string.IsNullOrWhiteSpace(models.Architect) || (strategies.Any(s => !s.WorkerIsStrong) && string.IsNullOrWhiteSpace(models.Worker)))
        {
            Output.Error(console, $"Set models.architect and models.worker in {Path.Combine(config.ConfigDirectory, TaskrailConfig.PolicyFile)}.");
            return ExitCodes.ConfigError;
        }

        var tasks = taskset.Tasks.Where(t => settings.Tasks is null || settings.Tasks.Contains(t.Id)).ToList();
        var trials = tasks.Count * strategies.Count * settings.Repeat;
        var ceiling = trials * config.Policy.Budget.MaxUsdPerTask;
        console.MarkupLine($"{tasks.Count} task(s) × {strategies.Count} strategies × {settings.Repeat} repeat(s) = [bold]{trials}[/] trials");
        console.MarkupLine(string.Create(CultureInfo.InvariantCulture,
            $"Budget ceiling: ${ceiling:0.00} (max_usd_per_task ${config.Policy.Budget.MaxUsdPerTask:0.00} per trial)"));
        if (settings.DryRun)
        {
            foreach (var s in strategies)
            {
                console.MarkupLine($"  {s.Id}: {Markup.Escape(s.Description)}");
            }

            return ExitCodes.Ok;
        }

        var prices = Services.LoadPrices(config);
        var key = services.GetEnvironmentVariable(config.Policy.Provider.ApiKeyEnv);
        IProvider? provider = strategies.Any(s => s.UsesArchitectPlan) ? services.ProviderFactory(config, prices) : null;

        using var ledger = new SqliteLedger(config.LedgerPath);
        var bench = new BenchRunner(runner, ledger, new Supervisor(), provider, services.ExecutorFactory);
        var benchId = await bench.RunAsync(taskset, config, new BenchOptions
        {
            Strategies = strategies,
            Repeat = settings.Repeat,
            TaskFilter = settings.Tasks,
        }, prices, key, new ConsoleBenchObserver(console), cancellationToken);

        var results = ledger.ListBenchResults(benchId);
        var markdown = BenchReport.Markdown(benchId, results, strategies.Select(s => s.Id).ToList());
        var reportPath = Path.Combine(config.ConfigDirectory, "runs", benchId + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, markdown, cancellationToken);

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

internal sealed class ConsoleBenchObserver(IAnsiConsole console) : IBenchObserver
{
    public void TrialStarted(BenchTask task, Strategy strategy, int repeat, int index, int total) =>
        console.MarkupLine($"[grey][[{index}/{total}]][/] {Markup.Escape(task.Id)}  strategy {strategy.Id}  repeat {repeat}");

    public void TrialFinished(BenchResultRecord result) =>
        console.MarkupLine(string.Create(CultureInfo.InvariantCulture,
            $"  {(result.Accepted ? "[green]accepted[/]" : "[red]rejected[/]")}  run {result.RunStatus}  ${result.Usd:0.0000}") +
            (result.Note is null ? "" : $"  [grey]{Markup.Escape(result.Note)}[/]"));
}
