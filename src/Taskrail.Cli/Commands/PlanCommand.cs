using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using Taskrail.Cli.Infrastructure;
using Taskrail.Core.Model;
using Taskrail.Llm.Architect;
using Taskrail.Llm.Providers;
using Taskrail.Runtime.Git;
using Taskrail.Runtime.Ledger;
using Taskrail.Runtime.Orchestration;
using Taskrail.Runtime.Process;

namespace Taskrail.Cli.Commands;

public sealed class PlanSettings : CommonSettings
{
    [CommandArgument(0, "<TASK>")]
    [Description("Task description for the architect.")]
    public string Task { get; init; } = "";

    [CommandOption("-s|--strategy <ID>")]
    [Description("1, 1b, 2, 3 or 4 (default). Strategies 1, 1b and 2 skip the architect and use the task as a single unit.")]
    [DefaultValue("4")]
    public string Strategy { get; init; } = "4";
}

public sealed partial class PlanCommand(IAnsiConsole console, IProcessRunner runner, Services services) : AsyncCommand<PlanSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PlanSettings settings, CancellationToken cancellationToken)
    {
        var config = ConfigLocator.LoadOrReport(console, settings, out var exit);
        if (config is null)
        {
            return exit;
        }

        Strategy strategy;
        try
        {
            strategy = Strategy.Parse(settings.Strategy);
        }
        catch (ArgumentException ex)
        {
            Output.Error(console, ex.Message);
            return ExitCodes.Usage;
        }

        var models = config.Policy.Models;
        if (string.IsNullOrWhiteSpace(models.Architect) || (!strategy.WorkerIsStrong && string.IsNullOrWhiteSpace(models.Worker)))
        {
            Output.Error(console, "Set models.architect and models.worker in policy.yaml first.");
            return ExitCodes.ConfigError;
        }

        var git = new GitCli(runner);
        string baseSha;
        try
        {
            baseSha = await git.HeadAsync(config.RepoRoot, cancellationToken);
            if (!await git.IsCleanAsync(config.RepoRoot, cancellationToken))
            {
                Output.Warn(console, "Uncommitted changes are not part of the plan; the run starts from HEAD.");
            }
        }
        catch (GitException ex)
        {
            Output.Error(console, $"{config.RepoRoot} must be a git repository with at least one commit. {ex.Message}");
            return ExitCodes.Failed;
        }

        var prices = Services.LoadPrices(config);
        var runId = RunArtifacts.NewRunId(DateTimeOffset.UtcNow);
        using var ledger = new SqliteLedger(config.LedgerPath);
        ledger.StartRun(new RunRecord
        {
            Id = runId,
            Strategy = strategy.Id,
            TaskId = Slug(settings.Task),
            Repo = config.RepoRoot,
            BaseSha = baseSha,
            Status = RunStatus.Planned,
            Started = DateTimeOffset.UtcNow,
        });

        PlannedTask planned;
        try
        {
            var provider = strategy.UsesArchitectPlan ? services.ProviderFactory(config, prices) : null;
            if (strategy.UsesArchitectPlan)
            {
                console.MarkupLine($"Planning with [bold]{Markup.Escape(models.Architect)}[/]...");
            }

            planned = await TaskPlanning.PlanAsync(strategy, settings.Task, config.RepoRoot, baseSha, runId, provider, models.Architect, ledger, git, cancellationToken);
        }
        catch (Exception ex) when (ex is PlanningException or MissingApiKeyException or HttpRequestException or InvalidOperationException)
        {
            ledger.FinishRun(runId, RunStatus.Error, DateTimeOffset.UtcNow);
            Output.Error(console, ex.Message);
            return ExitCodes.Failed;
        }

        var artifacts = new RunArtifacts(config.ConfigDirectory, runId);
        artifacts.SavePlan(new PlanFile
        {
            RunId = runId,
            Task = settings.Task,
            Strategy = strategy.Id,
            BaseSha = baseSha,
            Created = DateTimeOffset.UtcNow,
            Contract = planned.Contract,
        });

        if (settings.Json)
        {
            Output.Json(console, new { run_id = runId, strategy = strategy.Id, base_sha = baseSha, architect_usd = planned.ArchitectUsd, contract = planned.Contract });
            return ExitCodes.Ok;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Unit", "Title", "Depends on", "Files");
        foreach (var unit in planned.Contract.Units)
        {
            table.AddRow(Markup.Escape(unit.Id), Markup.Escape(unit.Title), Markup.Escape(string.Join(", ", unit.DependsOn)),
                Markup.Escape(string.Join(", ", unit.Files.Take(4))));
        }

        console.Write(table);
        foreach (var rule in planned.Contract.Rules)
        {
            console.MarkupLine($"  [grey]{Markup.Escape(rule.Id)}[/] {Markup.Escape(rule.Text)}");
        }

        if (strategy.UsesArchitectPlan)
        {
            console.MarkupLine(string.Create(CultureInfo.InvariantCulture,
                $"Architect cost: ${planned.ArchitectUsd:0.0000}{(planned.CostEstimated ? " (est.)" : "")}"));
        }

        console.MarkupLine($"Plan [bold]{runId}[/] saved to {Markup.Escape(Path.GetRelativePath(config.RepoRoot, artifacts.PlanPath))}");
        console.MarkupLine($"Next: [bold]taskrail run[/] (or taskrail run --plan {runId})");
        return ExitCodes.Ok;
    }

    internal static string Slug(string task)
    {
        var slug = NonWord().Replace(task.ToLowerInvariant(), "-").Trim('-');
        return slug.Length <= 48 ? slug : slug[..48].TrimEnd('-');
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}
