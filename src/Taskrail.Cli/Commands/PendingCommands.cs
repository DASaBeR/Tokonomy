using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Taskrail.Cli.Infrastructure;

namespace Taskrail.Cli.Commands;

// Commands whose behavior lands in later milestones. They parse their real arguments now so the
// CLI surface is stable, and exit with a distinct code so scripts can tell "not yet" from "failed".

public sealed class PlanSettings : CommonSettings
{
    [CommandArgument(0, "<TASK>")]
    [Description("Task description for the architect.")]
    public string Task { get; init; } = "";
}

public sealed class RunSettings : CommonSettings
{
    [CommandOption("--plan <RUN_ID>")]
    [Description("Execute a previously generated plan. Defaults to the latest.")]
    public string? Plan { get; init; }
}

public sealed class BenchSettings : CommonSettings
{
    [CommandArgument(0, "<TASKSET>")]
    [Description("Path to a tasks.yaml taskset.")]
    public string Taskset { get; init; } = "";

    [CommandOption("--strategies <LIST>")]
    [Description("Comma-separated strategies: 1, 1b, 2, 3, 4.")]
    public string? Strategies { get; init; }

    [CommandOption("--repeat <N>")]
    [DefaultValue(3)]
    public int Repeat { get; init; } = 3;
}

public sealed class PlanCommand(IAnsiConsole console) : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings, CancellationToken cancellationToken) =>
        Pending.Report(console, "plan", "M1");
}

public sealed class RunCommand(IAnsiConsole console) : Command<RunSettings>
{
    public override int Execute(CommandContext context, RunSettings settings, CancellationToken cancellationToken) =>
        Pending.Report(console, "run", "M1");
}

public sealed class BenchCommand(IAnsiConsole console) : Command<BenchSettings>
{
    public override int Execute(CommandContext context, BenchSettings settings, CancellationToken cancellationToken) =>
        Pending.Report(console, "bench", "M1");
}

internal static class Pending
{
    public static int Report(IAnsiConsole console, string command, string milestone)
    {
        console.MarkupLine($"[yellow]`taskrail {command}` is not implemented yet (planned for {milestone}).[/]");
        return ExitCodes.NotImplemented;
    }
}
