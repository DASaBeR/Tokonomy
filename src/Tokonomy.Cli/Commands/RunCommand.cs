using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Cli.Commands;

public sealed class RunSettings : CommonSettings
{
    [CommandOption("--plan <RUN_ID>")]
    [Description("Execute a previously generated plan. Defaults to the most recent unexecuted plan.")]
    public string? Plan { get; init; }

    [CommandOption("--keep-worktree")]
    [Description("Keep the run worktree on disk for inspection (the run branch is always kept).")]
    public bool KeepWorktree { get; init; }
}

public sealed class RunCommand(IAnsiConsole console, IProcessRunner runner, Services services) : AsyncCommand<RunSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RunSettings settings, CancellationToken cancellationToken)
    {
        var config = ConfigLocator.LoadOrReport(console, settings, out var exit);
        if (config is null)
        {
            return exit;
        }

        using var ledger = new SqliteLedger(config.LedgerPath);
        var runId = settings.Plan ?? ledger.ListRuns(100).FirstOrDefault(r => r.Status == RunStatus.Planned)?.Id;
        if (runId is null)
        {
            Output.Error(console, "No unexecuted plan found. Run `tokonomy plan \"<task>\"` first.");
            return ExitCodes.Usage;
        }

        var record = ledger.GetRun(runId);
        if (record is null)
        {
            Output.Error(console, $"Run '{runId}' not found.");
            return ExitCodes.Usage;
        }

        if (record.Status != RunStatus.Planned)
        {
            Output.Error(console, $"Run '{runId}' was already executed ({record.Status}). Create a new plan.");
            return ExitCodes.Usage;
        }

        var policy = config.Policy;
        if (settings.KeepWorktree)
        {
            policy.Isolation.KeepWorktree = true;
        }

        var key = services.GetEnvironmentVariable(policy.Provider.ApiKeyEnv);
        var artifacts = new RunArtifacts(config.ConfigDirectory, runId, string.IsNullOrEmpty(key) ? [] : [key]);
        var plan = RunArtifacts.LoadPlan(artifacts.PlanPath);
        var strategy = Strategy.Parse(plan.Strategy);
        var executorName = policy.Executors.Default;
        var executor = config.Executors.Executors[executorName];

        console.MarkupLine($"Run [bold]{runId}[/]  strategy {strategy.Id} ({Markup.Escape(strategy.Description)})  executor {Markup.Escape(executorName)}");
        IProvider? architect = null;
        if (strategy.AllowEscalation)
        {
            try
            {
                architect = services.ProviderFactory(config, Services.LoadPrices(config));
            }
            catch (MissingApiKeyException ex)
            {
                Output.Warn(console, $"{ex.Message} Takeover is disabled for this run.");
            }
        }

        var orchestrator = new RunOrchestrator(runner, ledger, new Supervisor(), services.ExecutorFactory, architect: architect);
        RunOutcome outcome;
        try
        {
            outcome = await orchestrator.RunAsync(new RunRequest
            {
                RunId = runId,
                Contract = plan.Contract,
                RepoRoot = config.RepoRoot,
                BaseSha = plan.BaseSha,
                Policy = policy,
                ExecutorName = executorName,
                Executor = executor,
                Artifacts = artifacts,
                WorkerModel = strategy.WorkerIsStrong ? policy.Models.Architect : policy.Models.Worker,
                UseGates = strategy.UseGates,
                AllowEscalation = strategy.AllowEscalation,
                MaxAttempts = strategy.MaxAttempts,
                Prices = Services.LoadPrices(config),
                UpstreamKey = key,
                AlreadySpentUsd = record.TotalUsd,
            }, new ConsoleObserver(console), cancellationToken);
        }
        catch (GitException ex)
        {
            ledger.FinishRun(runId, RunStatus.Error, DateTimeOffset.UtcNow);
            Output.Error(console, ex.Message);
            return ExitCodes.Failed;
        }

        if (settings.Json)
        {
            Output.Json(console, outcome);
            return outcome.Status == RunStatus.Pass ? ExitCodes.Ok : ExitCodes.Failed;
        }

        console.WriteLine();
        var color = outcome.Status == RunStatus.Pass ? "green" : "red";
        console.MarkupLine($"[{color}]{outcome.Status.ToString().ToUpperInvariant()}[/]" +
                           (outcome.Error is null ? "" : $"  {Markup.Escape(outcome.Error)}"));
        console.MarkupLine(string.Create(CultureInfo.InvariantCulture, $"Spent: ${outcome.TotalUsd:0.0000}") +
                           (ledger.GetRun(runId)?.CostSource == CostSource.Estimated ? " [yellow](est.)[/]" : ""));
        console.MarkupLine($"Branch: [bold]{Markup.Escape(outcome.Branch)}[/]" +
                           (outcome.Status == RunStatus.Pass ? $"   merge with: git merge {Markup.Escape(outcome.Branch)}" : ""));
        if (outcome.WorktreePath is not null)
        {
            console.MarkupLine($"Worktree: {Markup.Escape(outcome.WorktreePath)}");
        }

        console.MarkupLine($"Details: tokonomy report {runId}");
        return outcome.Status == RunStatus.Pass ? ExitCodes.Ok : ExitCodes.Failed;
    }
}

internal sealed class ConsoleObserver(IAnsiConsole console) : IRunObserver
{
    public void Stage(string message) => console.MarkupLine($"[grey]› {Markup.Escape(message)}[/]");

    public void UnitStarted(WorkUnit unit) => console.MarkupLine($"[bold]{Markup.Escape(unit.Id)}[/] {Markup.Escape(unit.Title)}");

    public void AttemptFinished(WorkUnit unit, int attempt, RunResult run, UnitDiff diff, IReadOnlyList<GateResult> gates, Decision decision)
    {
        var gateText = gates.Count == 0
            ? ""
            : "  gates: " + string.Join(" ", gates.Select(g => g.Status switch
            {
                GateStatus.Passed => $"{Markup.Escape(g.Gate)} [green]✓[/]",
                GateStatus.PreExisting => $"{Markup.Escape(g.Gate)} [yellow]~[/]",
                _ => $"{Markup.Escape(g.Gate)} [red]✗[/]",
            }));
        var color = decision.Kind switch
        {
            DecisionKind.Accept => "green",
            DecisionKind.Retry => "yellow",
            _ => "red",
        };
        var exitText = run.TimedOut ? "timed out" : $"exit {run.ExitCode}";
        console.MarkupLine(
            $"  attempt {attempt}: {exitText}, {diff.TotalLines} line(s) in {diff.ChangedPaths.Count} file(s){gateText} → " +
            $"[{color}]{decision.Kind.ToString().ToUpperInvariant()}[/] [grey]{Markup.Escape(decision.Reason)}[/]");
    }

    public void Warning(string message) => Output.Warn(console, message);

    public void TakeoverFinished(WorkUnit unit, bool success, string note) =>
        console.MarkupLine(success
            ? $"  [green]architect takeover succeeded[/] [grey]{Markup.Escape(note)}[/]"
            : $"  [red]architect takeover failed[/] [grey]{Markup.Escape(note)}[/]");
}
