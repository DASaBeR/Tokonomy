using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Cli.Commands;

public sealed class BuildSettings : CommonSettings
{
    [CommandArgument(0, "[TASK]")]
    [Description("What you want built, in plain words. Asked for if left out.")]
    public string Task { get; init; } = "";

    [CommandOption("-y|--yes")]
    [Description("Do not ask: accept the defaults for first-time setup and start building right after planning.")]
    public bool Yes { get; init; }

    [CommandOption("--merge")]
    [Description("Merge the result into your current branch when the build passes, without asking.")]
    public bool Merge { get; init; }

    [CommandOption("--keep-worktree")]
    [Description("Keep the run worktree on disk for inspection (the run branch is always kept).")]
    public bool KeepWorktree { get; init; }
}

/// <summary>
/// The whole workflow behind one command: set up if this is the first time, make git ready, check the tools, plan,
/// show the plan and ask, build and verify every step, then offer to merge. Nothing to edit, nothing to run first.
/// </summary>
public sealed class BuildCommand(IAnsiConsole console, IProcessRunner runner, Services services) : AsyncCommand<BuildSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BuildSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Task) && !Ui.IsInteractive(console))
        {
            Output.Error(console, "Tell me what to build, e.g. tokonomy build \"a landing page for a SaaS product\".");
            return ExitCodes.Usage;
        }

        Ui.Banner(console, "build");
        var (config, exit) = await ConfigLocator.LoadOrSetupAsync(console, runner, settings, settings.Yes, cancellationToken, banner: false);
        if (config is null)
        {
            return exit;
        }

        var task = settings.Task;
        if (string.IsNullOrWhiteSpace(task))
        {
            task = Ui.ReadTask(console, "[bold]What do you want to build?[/]");
            console.WriteLine();
        }

        // 1. Everything this machine needs, before a single token is spent.
        var git = new GitCli(runner);
        await Ui.EnsureRepoAsync(console, git, config.RepoRoot, cancellationToken);
        var (failures, warnings) = await Ui.SpinAsync(console, "Checking your tools",
            () => new DoctorCommand(console, runner).PreflightAsync(config, cancellationToken));
        if (failures.Count > 0)
        {
            Ui.Problem(console, "Not ready yet:");
            foreach (var failure in failures)
            {
                console.MarkupLine($"    [red]•[/] {Markup.Escape(failure)}");
            }

            Ui.Note(console, "Fix these, then run the same command again. `tokonomy doctor` shows the full list.");
            return ExitCodes.Failed;
        }

        foreach (var warning in warnings.Where(w => !w.StartsWith("prices:", StringComparison.Ordinal)))
        {
            Output.Warn(console, warning);
        }

        Ui.Done(console, "Tools are ready.");

        // 2. Plan.
        var planner = new PlanCommand(console, runner, services);
        var (planExit, runId, planned) = await planner.PlanCoreAsync(config, new PlanSettings { Task = task, Strategy = "4", Config = config.ConfigDirectory, NoQuestions = settings.Yes }, cancellationToken);
        if (planned is null || runId is null)
        {
            return planExit;
        }

        console.WriteLine();
        planner.ShowPlan(config, runId, planned);

        // 3. Confirm.
        console.WriteLine();
        Ui.DiscardPendingInput();
        if (!settings.Yes && Ui.IsInteractive(console) && !console.Confirm("Build this now?", true))
        {
            console.MarkupLine($"Plan saved. Build it later with [bold]tokonomy run --plan {runId}[/]");
            return ExitCodes.Ok;
        }

        // 4. Build and verify.
        var runSettings = new RunSettings { Plan = runId, Config = config.ConfigDirectory, KeepWorktree = settings.KeepWorktree };
        var (runExit, outcome) = await new RunCommand(console, runner, services).RunPlanAsync(config, runSettings, quietHeader: true, cancellationToken);
        if (outcome is null || outcome.Status != RunStatus.Pass)
        {
            console.MarkupLine($"Look closer with [bold]tokonomy report {runId}[/]. The work so far is on branch [bold]{Markup.Escape(outcome?.Branch ?? "")}[/].");
            return runExit;
        }

        // 5. Bring it into the project.
        var merge = settings.Merge || (!settings.Yes && Ui.IsInteractive(console) &&
                                       console.Confirm($"Merge [bold]{Markup.Escape(outcome.Branch)}[/] into your current branch now?", true));
        if (!merge)
        {
            console.MarkupLine($"Merge it when you are ready: [bold]git merge {Markup.Escape(outcome.Branch)}[/]");
            return ExitCodes.Ok;
        }

        try
        {
            var before = await git.HeadAsync(config.RepoRoot, cancellationToken);
            await git.RunAsync(config.RepoRoot, ["merge", "--no-edit", outcome.Branch], cancellationToken);
            var changed = (await git.RunAsync(config.RepoRoot, ["diff", "--name-only", before, "HEAD"], cancellationToken))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(f => !f.StartsWith("acceptance/", StringComparison.Ordinal))
                .ToList();
            Ui.Done(console, $"Merged. {changed.Count} file(s) added or changed:");
            foreach (var file in changed.Take(12))
            {
                Ui.Note(console, file);
            }

            if (changed.Count > 12)
            {
                Ui.Note(console, $"... and {changed.Count - 12} more");
            }
        }
        catch (GitException ex)
        {
            Ui.Problem(console, "The merge did not go through, so nothing was changed in your project.");
            Ui.Note(console, ex.Message);
            console.MarkupLine($"Do it by hand when ready: [bold]git merge {Markup.Escape(outcome.Branch)}[/]");
            await git.RunAsync(config.RepoRoot, ["merge", "--abort"], cancellationToken, allowFailure: true);
        }

        return ExitCodes.Ok;
    }
}
