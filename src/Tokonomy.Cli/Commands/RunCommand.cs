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

    [CommandOption("--again")]
    [Description("With --plan: run a plan that was already executed (for example after a failed run) as a new run, without planning again.")]
    public bool Again { get; init; }

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

        var (code, _) = await RunPlanAsync(config, settings, quietHeader: false, cancellationToken);
        return code;
    }

    /// <summary>A new, not yet executed run that carries the saved plan (contract, tests and base commit) of <paramref name="source"/>.</summary>
    private static string CopyPlanToNewRun(Tokonomy.Runtime.Config.TokonomyConfig config, SqliteLedger ledger, RunRecord source)
    {
        var plan = RunArtifacts.LoadPlan(new RunArtifacts(config.ConfigDirectory, source.Id).PlanPath);
        var newId = RunArtifacts.NewRunId(DateTimeOffset.UtcNow);
        ledger.StartRun(new RunRecord
        {
            Id = newId,
            Strategy = source.Strategy,
            TaskId = source.TaskId,
            Repo = source.Repo,
            BaseSha = plan.BaseSha,
            Status = RunStatus.Planned,
            Started = DateTimeOffset.UtcNow,
        });
        new RunArtifacts(config.ConfigDirectory, newId).SavePlan(plan with { RunId = newId, Created = DateTimeOffset.UtcNow });
        return newId;
    }

    /// <summary>Executes a saved plan. Returns the exit code and the outcome (null when the run could not start).</summary>
    internal async Task<(int Exit, RunOutcome? Outcome)> RunPlanAsync(
        Tokonomy.Runtime.Config.TokonomyConfig config, RunSettings settings, bool quietHeader, CancellationToken cancellationToken)
    {
        using var ledger = new SqliteLedger(config.LedgerPath);
        var runId = settings.Plan ?? ledger.ListRuns(100).FirstOrDefault(r => r.Status == RunStatus.Planned)?.Id;
        if (runId is null)
        {
            Output.Error(console, "No unexecuted plan found. Run `tokonomy plan \"<task>\"` first.");
            return (ExitCodes.Usage, null);
        }

        var record = ledger.GetRun(runId);
        if (record is null)
        {
            Output.Error(console, $"Run '{runId}' not found.");
            return (ExitCodes.Usage, null);
        }

        if (record.Status != RunStatus.Planned)
        {
            if (!settings.Again)
            {
                Output.Error(console, $"Run '{runId}' was already executed ({record.Status}). Create a new plan, or run the same plan again with `tokonomy run --plan {runId} --again` (no new planning cost).");
                return (ExitCodes.Usage, null);
            }

            // A run is a one-off record (its branch, worktree and ledger rows), so "again" is a fresh run of the same plan.
            var again = CopyPlanToNewRun(config, ledger, record);
            Ui.Note(console, $"Running the plan of {runId} again as {again}. Planning cost stays with the original run.");
            runId = again;
            record = ledger.GetRun(runId)!;
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
        var executorName = policy.Executors.For(strategy.WorkerIsStrong);
        var executor = config.Executors.Executors[executorName];

        if (!quietHeader)
        {
            Ui.Banner(console, $"run {runId}");
        }

        Ui.Note(console, $"strategy {strategy.Id} ({strategy.Description}) · executor {executorName} · worker {(strategy.WorkerIsStrong ? policy.Models.Architect : policy.Models.Worker)}");
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
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var observer = new ConsoleObserver(console, plan.Contract);
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
            }, observer, cancellationToken);
        }
        catch (GitException ex)
        {
            ledger.FinishRun(runId, RunStatus.Error, DateTimeOffset.UtcNow);
            Output.Error(console, ex.Message);
            return (ExitCodes.Failed, null);
        }

        observer.Dispose();
        if (settings.Json)
        {
            Output.Json(console, outcome);
            return (outcome.Status == RunStatus.Pass ? ExitCodes.Ok : ExitCodes.Failed, outcome);
        }

        var estimated = ledger.GetRun(runId)?.CostSource == CostSource.Estimated;
        Ui.RunSummary(console, outcome, runId, estimated, clock.Elapsed);
        if (outcome.WorktreePath is not null)
        {
            Ui.Note(console, $"worktree kept at {outcome.WorktreePath}");
        }

        if (outcome.Review is { } review)
        {
            var reviewColor = review.Verdict == Tokonomy.Core.Model.ReviewVerdict.Approve ? "green" : "yellow";
            console.MarkupLine($"Architect review (advisory): [{reviewColor}]{review.Verdict}[/] {Markup.Escape(review.Summary)}");
            foreach (var concern in review.Concerns)
            {
                console.MarkupLine($"  - {Markup.Escape(concern)}");
            }
        }

        if (!quietHeader)
        {
            console.MarkupLine(outcome.Status == RunStatus.Pass
                ? $"Merge it with: [bold]git merge {Markup.Escape(outcome.Branch)}[/]   ·   details: tokonomy report {runId}"
                : $"Details: tokonomy report {runId}");
        }

        return (outcome.Status == RunStatus.Pass ? ExitCodes.Ok : ExitCodes.Failed, outcome);
    }
}
