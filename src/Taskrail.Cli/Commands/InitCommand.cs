using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Taskrail.Cli.Infrastructure;
using Taskrail.Runtime.Config;

namespace Taskrail.Cli.Commands;

public sealed class InitSettings : CommonSettings
{
    [CommandOption("-p|--path <DIR>")]
    [Description("Repository root. Defaults to the current directory.")]
    public string? Path { get; init; }

    [CommandOption("-f|--force")]
    [Description("Overwrite existing config files.")]
    public bool Force { get; init; }
}

public sealed class InitCommand(IAnsiConsole console) : Command<InitSettings>
{
    public override int Execute(CommandContext context, InitSettings settings, CancellationToken cancellationToken)
    {
        var root = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root))
        {
            Output.Error(console, $"Directory not found: {root}");
            return ExitCodes.Usage;
        }

        var notGit = !Directory.Exists(System.IO.Path.Combine(root, ".git")) && !File.Exists(System.IO.Path.Combine(root, ".git"));
        var result = Scaffolder.Init(root, settings.Force);

        if (settings.Json)
        {
            Output.Json(console, new
            {
                config_directory = result.ConfigDirectory,
                stacks = result.Stacks.Select(s => new { s.Name, s.Marker, gates = s.Gates.Select(g => g.Cmd) }),
                written = result.Written,
                skipped = result.Skipped,
                not_a_git_repo = notGit,
            });
            return ExitCodes.Ok;
        }

        if (notGit)
        {
            Output.Warn(console, $"{root} is not a git repository root. Taskrail needs git to isolate and diff work.");
        }

        console.MarkupLine(result.Stacks.Count == 0
            ? "[yellow]No known stack detected.[/] Add gates to policy.yaml by hand."
            : $"Detected: [green]{Markup.Escape(string.Join(", ", result.Stacks.Select(s => $"{s.Name} ({s.Marker})")))}[/]");

        foreach (var file in result.Written)
        {
            console.MarkupLine($"  [green]wrote[/]   {Markup.Escape(System.IO.Path.Combine(TaskrailConfig.DirectoryName, file))}");
        }

        foreach (var file in result.Skipped)
        {
            console.MarkupLine($"  [grey]kept[/]    {Markup.Escape(System.IO.Path.Combine(TaskrailConfig.DirectoryName, file))} (use --force to overwrite)");
        }

        console.WriteLine();
        console.MarkupLine("Next: set [bold]models[/] in policy.yaml, add prices to prices.yaml, then run [bold]taskrail doctor[/].");
        return ExitCodes.Ok;
    }
}
