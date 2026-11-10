using Spectre.Console;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Orchestration;

namespace Tokonomy.Cli.Infrastructure;

/// <summary>Live progress for `run` and `build`: a header per unit, a spinner while the worker or the gates work, one result line per attempt.</summary>
internal sealed class ConsoleObserver(IAnsiConsole console, Contract? contract = null) : IRunObserver, IDisposable
{
    private readonly Working _working = new(console);

    public void Stage(string message)
    {
        _working.Stop();
        console.MarkupLine($"[grey]› {Markup.Escape(message)}[/]");
    }

    public void UnitStarted(WorkUnit unit)
    {
        _working.Stop();
        var position = contract is null ? "" : $"  [grey]({contract.Units.ToList().FindIndex(u => u.Id == unit.Id) + 1}/{contract.Units.Count})[/]";
        console.WriteLine();
        console.Write(new Spectre.Console.Rule($"[bold blue]{Markup.Escape(unit.Id)}[/]  [bold]{Markup.Escape(unit.Title)}[/]{position}")
        {
            Justification = Justify.Left,
            Style = Style.Parse("grey"),
        });
    }

    public void AttemptStarted(WorkUnit unit, int attempt) =>
        _working.Start(attempt == 1 ? $"{unit.Id}: the worker is writing code" : $"{unit.Id}: the worker is fixing it (attempt {attempt})");

    public void GatesStarted(WorkUnit unit) => _working.Start($"{unit.Id}: checking the result (gates and acceptance tests)");

    public void AttemptFinished(WorkUnit unit, int attempt, RunResult run, UnitDiff diff, IReadOnlyList<GateResult> gates, Decision decision)
    {
        _working.Stop();
        var gateText = gates.Count == 0
            ? ""
            : "  " + string.Join("  ", gates.Select(g => g.Status switch
            {
                GateStatus.Passed => $"[green]✓[/] {Markup.Escape(g.Gate)}",
                GateStatus.PreExisting => $"[yellow]~[/] {Markup.Escape(g.Gate)}",
                _ => $"[red]✗[/] {Markup.Escape(g.Gate)}",
            }));
        var (mark, color, verdict) = decision.Kind switch
        {
            DecisionKind.Accept => ("✓", "green", "accepted"),
            DecisionKind.Retry => ("↻", "yellow", "retrying"),
            _ => ("✗", "red", "failed"),
        };
        var how = run.TimedOut ? "timed out" : $"{diff.ChangedPaths.Count} file(s), {diff.TotalLines} line(s)";
        console.MarkupLine($"  [{color}]{mark}[/] attempt {attempt}: {how}{gateText}  [{color}]{verdict}[/]");
        if (decision.Kind != DecisionKind.Accept)
        {
            console.MarkupLine($"    [grey]{Markup.Escape(Ui.Shorten(decision.Reason, 200))}[/]");
        }

        foreach (var failed in gates.Where(g => g.Status == GateStatus.Failed && !string.IsNullOrWhiteSpace(g.Output)).Take(1))
        {
            var tail = string.Join('\n', failed.Output!.Split('\n').TakeLast(6).Select(l => "      " + Markup.Escape(Ui.Shorten(l, 160))));
            console.MarkupLine($"    [grey]{Markup.Escape(failed.Gate)} output:[/]\n[grey]{tail}[/]");
        }
    }

    public void Warning(string message)
    {
        _working.Stop();
        Output.Warn(console, message);
    }

    public void TakeoverFinished(WorkUnit unit, bool success, string note)
    {
        _working.Stop();
        console.MarkupLine(success
            ? $"  [green]✓ the architect took over and fixed it[/] [grey]{Markup.Escape(Ui.Shorten(note, 160))}[/]"
            : $"  [red]✗ the architect could not fix it[/] [grey]{Markup.Escape(Ui.Shorten(note, 160))}[/]");
    }

    public void Dispose() => _working.Dispose();
}
