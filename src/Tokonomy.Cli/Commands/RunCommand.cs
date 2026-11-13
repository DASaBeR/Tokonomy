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

    [CommandOption("--resume <RUN_ID>")]
    [Description("Continue an interrupted run (crash, Ctrl+C, closed terminal) on its own branch: completed units are kept and checked, the interrupted unit restarts from its last verified commit.")]
    public string? Resume { get; init; }

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

    /// <summary>
    /// Checks that a run is resumable and loads its saved state, keeping load errors (a corrupt or truncated file) for
    /// the orchestrator's reconciliation to judge rather than failing here. Returns an error message or the request.
    /// </summary>
    internal static (string? Problem, ResumeRequest? Request) PrepareResume(Tokonomy.Runtime.Config.TokonomyConfig config, SqliteLedger ledger, string runId)
    {
        var record = ledger.GetRun(runId);
        if (record is null)
        {
            return ($"Run '{runId}' not found.", null);
        }

        switch (record.Status)
        {
            case RunStatus.Planned:
                return ($"Run '{runId}' never started; run it with `tokonomy run --plan {runId}`.", null);
            case RunStatus.Pass:
                return ($"Run '{runId}' already passed; there is nothing to resume.", null);
            case RunStatus.Fail:
                return ($"Run '{runId}' failed on a verdict (a unit failed its gates or its escalation), not an interruption. Run the plan again with `tokonomy run --plan {runId} --again`.", null);
            case RunStatus.AbortedBudget when record.TotalUsd >= config.Policy.Budget.MaxUsdPerTask:
                return ($"Run '{runId}' stopped at its budget (${record.TotalUsd:0.00} spent of budget.max_usd_per_task ${config.Policy.Budget.MaxUsdPerTask:0.00}). Raise the budget first if you want to resume it.", null);
        }

        var artifacts = new RunArtifacts(config.ConfigDirectory, runId);
        if (!File.Exists(artifacts.PlanPath))
        {
            return ($"Run '{runId}' has no saved plan ({artifacts.PlanPath}).", null);
        }

        Tokonomy.Core.Planning.PlanState? planState = null;
        string? planError = null;
        try
        {
            planState = artifacts.LoadPlanState();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or NotSupportedException)
        {
            planError = ex.Message;
        }

        RunState? runState = null;
        string? runError = null;
        try
        {
            runState = artifacts.LoadRunState();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or NotSupportedException)
        {
            runError = ex.Message;
        }

        return (null, new ResumeRequest { PlanState = planState, PlanStateError = planError, RunState = runState, RunStateError = runError });
    }

    /// <summary>Executes a saved plan. Returns the exit code and the outcome (null when the run could not start).</summary>
    internal async Task<(int Exit, RunOutcome? Outcome)> RunPlanAsync(
        Tokonomy.Runtime.Config.TokonomyConfig config, RunSettings settings, bool quietHeader, CancellationToken cancellationToken)
    {
        using var ledger = new SqliteLedger(config.LedgerPath);
        if (settings.Resume is not null && (settings.Plan is not null || settings.Again))
        {
            Output.Error(console, "--resume cannot be combined with --plan or --again.");
            return (ExitCodes.Usage, null);
        }

        ResumeRequest? resume = null;
        if (settings.Resume is { } resumeId)
        {
            var (problem, request) = PrepareResume(config, ledger, resumeId);
            if (problem is not null)
            {
                Output.Error(console, problem);
                return (ExitCodes.Usage, null);
            }

            resume = request;
        }

        var runId = settings.Resume ?? settings.Plan ?? ledger.ListRuns(100).FirstOrDefault(r => r.Status == RunStatus.Planned)?.Id;
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

        if (record.Status != RunStatus.Planned && resume is null)
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
        if (plan.Readiness is { } readiness && readiness != Tokonomy.Core.Planning.PlanReadiness.Ready)
        {
            Output.Warn(console, $"This plan was {Ui.Readiness(readiness)} when it was made ({plan.Issues.Count(i => !i.StartsWith("(advice)", StringComparison.Ordinal))} issue(s)); the gates still decide every unit.");
        }
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
                AdaptiveSupervision = strategy.AdaptiveSupervision,
                MaxAttempts = strategy.MaxAttempts,
                Prices = Services.LoadPrices(config),
                UpstreamKey = key,
                AlreadySpentUsd = record.TotalUsd,
                Resume = resume,
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

        if (outcome.Interventions > 0 || outcome.PlanRevision > 1)
        {
            Ui.Note(console, $"architect interventions: {outcome.Interventions} · plan revision {outcome.PlanRevision} · lessons {outcome.Lessons}  (details: tokonomy report {runId})");
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
