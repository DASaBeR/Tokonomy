using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Cli.Commands;

public sealed class InitSettings : CommonSettings
{
    [CommandOption("-p|--path <DIR>")]
    [Description("Repository root. Defaults to the current directory.")]
    public string? Path { get; init; }

    [CommandOption("-f|--force")]
    [Description("Overwrite existing config files.")]
    public bool Force { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Do not ask questions: use the flags below, then defaults (the best setup for the tools installed here).")]
    public bool Yes { get; init; }

    [CommandOption("--preset <NAME>")]
    [Description("Shortcut for both roles: claude-antigravity, claude (Claude does both) or api (OpenRouter does both).")]
    public string? Preset { get; init; }

    [CommandOption("--strong <SOURCE>")]
    [Description("Where the strong model comes from: claude (Claude Code subscription), antigravity (Antigravity subscription), codex (Codex CLI: ChatGPT plan or API key), custom (see --custom-url), or an API: openrouter, anthropic, gemini, openai, deepseek.")]
    public string? Strong { get; init; }

    [CommandOption("--cheap <SOURCE>")]
    [Description("Where the cheap model comes from: same values as --strong.")]
    public string? Cheap { get; init; }

    [CommandOption("--custom-url <URL>")]
    [Description("With --strong/--cheap custom: the provider's OpenAI-compatible base URL, e.g. https://api.avalai.ir/v1.")]
    public string? CustomUrl { get; init; }

    [CommandOption("--custom-key-env <NAME>")]
    [Description("With --strong/--cheap custom: the NAME of the environment variable that holds its API key.")]
    public string? CustomKeyEnv { get; init; }

    [CommandOption("--custom-name <NAME>")]
    [Description("With --strong/--cheap custom: what to call the provider (default: its host name).")]
    public string? CustomName { get; init; }

    [CommandOption("--architect <MODEL>")]
    [Description("Strong model id, e.g. claude-opus-5-5 or anthropic/claude-sonnet-5-5.")]
    public string? Architect { get; init; }

    [CommandOption("--worker <MODEL>")]
    [Description("Cheap model id, e.g. gemini-3.8-flash-medium (see `agy models`) or deepseek/deepseek-v4.1-flash.")]
    public string? Worker { get; init; }

    [CommandOption("--isolation <MODE>")]
    [Description("Where the worker's code is tested: windows-sandbox (safe, default) or host-unsafe (directly on this computer).")]
    public string? Isolation { get; init; }

    [CommandOption("--worker-shell <MODE>")]
    [Description("What shell commands the model that writes the code may run: restricted (default: only what Tokonomy needs) or all (ANY command; it can do anything your user account can).")]
    public string? WorkerShell { get; init; }

    [CommandOption("--tests <on|off>")]
    [Description("Let the strong model write the acceptance tests (default: on when the project has no test setup).")]
    public string? Tests { get; init; }
}

public sealed class InitCommand(IAnsiConsole console, IProcessRunner runner, Services services) : AsyncCommand<InitSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InitSettings settings, CancellationToken cancellationToken)
    {
        var root = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root))
        {
            Output.Error(console, $"Directory not found: {root}");
            return ExitCodes.Usage;
        }

        if (settings.Tests is not (null or "on" or "off"))
        {
            Output.Error(console, "--tests must be 'on' or 'off'.");
            return ExitCodes.Usage;
        }

        if (settings.WorkerShell is not (null or "restricted" or "all"))
        {
            Output.Error(console, "--worker-shell must be 'restricted' or 'all'.");
            return ExitCodes.Usage;
        }

        var options = new SetupOptions
        {
            WorkerShell = settings.WorkerShell,
            Preset = settings.Preset,
            Strong = settings.Strong,
            Cheap = settings.Cheap,
            CustomUrl = settings.CustomUrl,
            CustomKeyEnv = settings.CustomKeyEnv,
            CustomName = settings.CustomName,
            Architect = settings.Architect,
            Worker = settings.Worker,
            Isolation = settings.Isolation,
            Acceptance = settings.Tests is null ? null : settings.Tests == "on",
            Yes = settings.Yes || settings.Json,
            Force = settings.Force,
        };

        if (settings.Json)
        {
            var git = new GitCli(runner);
            var boot = await RepoBootstrap.EnsureAsync(git, root, cancellationToken);
            var quiet = new SetupWizard(AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(TextWriter.Null) }), runner);
            var scaffolded = await quiet.RunAsync(root, options, cancellationToken);
            if (scaffolded is null)
            {
                Output.Error(console, "Setup could not choose who plans and who codes on this machine. Pass --strong and --cheap (for example --strong claude --cheap antigravity), or run `tokonomy init` interactively.");
                return ExitCodes.Usage;
            }

            Output.Json(console, new
            {
                config_directory = scaffolded.ConfigDirectory,
                stacks = scaffolded.Stacks.Select(s => new { s.Name, s.Marker, gates = s.Gates.Select(g => g.Cmd) }),
                written = scaffolded.Written,
                skipped = scaffolded.Skipped,
                git_initialized = boot.InitializedRepo,
                initial_commit_created = boot.CreatedInitialCommit,
            });
            return ExitCodes.Ok;
        }

        Ui.Banner(console, "setup");
        await Ui.EnsureRepoAsync(console, new GitCli(runner), root, cancellationToken);
        var result = await new SetupWizard(console, runner).RunAsync(root, options, cancellationToken);
        if (result is null)
        {
            return ExitCodes.Usage;
        }

        console.WriteLine();
        if (Ui.IsInteractive(console) && !options.Yes)
        {
            var task = Ui.ReadTask(console, "[bold]What do you want to build?[/] [grey](Enter to skip; you can run `tokonomy build \"...\"` any time)[/]", allowEmpty: true);
            if (!string.IsNullOrWhiteSpace(task))
            {
                return await new BuildCommand(console, runner, services).ExecuteAsync(
                    context, new BuildSettings { Task = task.Trim(), Config = result.ConfigDirectory }, cancellationToken);
            }
        }

        console.MarkupLine("Next: [bold]tokonomy build \"what you want built\"[/]");
        console.MarkupLine("[grey]It plans, asks you to confirm, then builds and verifies each step.[/]");
        return ExitCodes.Ok;
    }
}
