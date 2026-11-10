using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Model;
using Tokonomy.Llm.Architect;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Cli.Commands;

public sealed class PlanSettings : CommonSettings
{
    [CommandArgument(0, "<TASK>")]
    [Description("Task description for the architect.")]
    public string Task { get; init; } = "";

    [CommandOption("-s|--strategy <ID>")]
    [Description("1, 1b, 2, 3 or 4 (default). Strategies 1, 1b and 2 skip the architect and use the task as a single unit.")]
    [DefaultValue("4")]
    public string Strategy { get; init; } = "4";

    [CommandOption("--no-questions")]
    [Description("Never let the architect ask clarifying questions; it plans with its own assumptions.")]
    public bool NoQuestions { get; init; }
}

public sealed partial class PlanCommand(IAnsiConsole console, IProcessRunner runner, Services services) : AsyncCommand<PlanSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PlanSettings settings, CancellationToken cancellationToken)
    {
        var (config, exit) = await ConfigLocator.LoadOrSetupAsync(console, runner, settings, yes: false, cancellationToken);
        if (config is null)
        {
            return exit;
        }

        var (code, runId, planned) = await PlanCoreAsync(config, settings, cancellationToken);
        if (planned is null || settings.Json)
        {
            return code;
        }

        ShowPlan(config, runId!, planned);
        console.MarkupLine($"Next: [bold]tokonomy run[/] (or tokonomy run --plan {runId})");
        return ExitCodes.Ok;
    }

    internal void ShowPlan(Tokonomy.Runtime.Config.TokonomyConfig config, string runId, PlannedTask planned)
    {
        Ui.PlanView(console, runId, planned.Contract);
        if (planned.ArchitectUsd > 0 || config.Policy.ArchitectProvider.Kind != Tokonomy.Core.Config.ArchitectProviderConfig.ApiKind)
        {
            Ui.Note(console, $"planning cost: {Ui.Money(planned.ArchitectUsd, planned.CostEstimated)}");
        }

        Ui.Note(console, $"saved: {Path.GetRelativePath(config.RepoRoot, new RunArtifacts(config.ConfigDirectory, runId).PlanPath)}");
    }

    /// <summary>Makes sure git is ready, asks the architect for a plan and saves it. Returns the new run id and the plan.</summary>
    internal async Task<(int Exit, string? RunId, PlannedTask? Planned)> PlanCoreAsync(
        Tokonomy.Runtime.Config.TokonomyConfig config, PlanSettings settings, CancellationToken cancellationToken)
    {
        Strategy strategy;
        try
        {
            strategy = Strategy.Parse(settings.Strategy);
        }
        catch (ArgumentException ex)
        {
            Output.Error(console, ex.Message);
            return (ExitCodes.Usage, null, null);
        }

        var models = config.Policy.Models;
        if (string.IsNullOrWhiteSpace(models.Architect) || (!strategy.WorkerIsStrong && string.IsNullOrWhiteSpace(models.Worker)))
        {
            Output.Error(console, "Set models.architect and models.worker in policy.yaml first (or run `tokonomy init --force` to redo the setup).");
            return (ExitCodes.ConfigError, null, null);
        }

        var git = new GitCli(runner);
        string baseSha;
        try
        {
            baseSha = await Ui.EnsureRepoAsync(console, git, config.RepoRoot, cancellationToken);
            if (!await git.IsCleanAsync(config.RepoRoot, cancellationToken))
            {
                Output.Warn(console, "Uncommitted changes are not part of the plan; the run starts from HEAD.");
            }
        }
        catch (GitException ex)
        {
            Output.Error(console, $"Could not prepare the git repository in {config.RepoRoot}: {ex.Message}");
            return (ExitCodes.Failed, null, null);
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
            var canAsk = strategy.UsesArchitectPlan && !settings.Json && !settings.NoQuestions && Ui.IsInteractive(console);
            if (canAsk)
            {
                // Questions are prompts, which cannot run inside a spinner, so each architect call spins on its own.
                var spinning = new SpinningProvider(provider!, console, $"{models.Architect} is planning (usually 30-90 seconds)");
                planned = await TaskPlanning.PlanAsync(strategy, settings.Task, config.RepoRoot, baseSha, runId, spinning, models.Architect, ledger, git, cancellationToken,
                    config.Policy.Acceptance, ClarifyPrompt.For(console));
            }
            else
            {
                planned = strategy.UsesArchitectPlan && !settings.Json
                    ? await Ui.SpinAsync(console, $"{models.Architect} is planning (usually 30-90 seconds)", () =>
                        TaskPlanning.PlanAsync(strategy, settings.Task, config.RepoRoot, baseSha, runId, provider, models.Architect, ledger, git, cancellationToken, config.Policy.Acceptance))
                    : await TaskPlanning.PlanAsync(strategy, settings.Task, config.RepoRoot, baseSha, runId, provider, models.Architect, ledger, git, cancellationToken, config.Policy.Acceptance);
            }
        }
        catch (Exception ex) when (ex is PlanningException or MissingApiKeyException or HttpRequestException or InvalidOperationException)
        {
            ledger.FinishRun(runId, RunStatus.Error, DateTimeOffset.UtcNow);
            Output.Error(console, ex.Message);
            return (ExitCodes.Failed, null, null);
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
        }

        return (ExitCodes.Ok, runId, planned);
    }

    internal static string Slug(string task)
    {
        var slug = NonWord().Replace(task.ToLowerInvariant(), "-").Trim('-');
        return slug.Length <= 48 ? slug : slug[..48].TrimEnd('-');
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}
