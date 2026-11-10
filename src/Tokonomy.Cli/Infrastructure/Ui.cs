using System.Diagnostics;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Orchestration;

namespace Tokonomy.Cli.Infrastructure;

/// <summary>Console building blocks shared by the commands, so every screen looks and reads the same.</summary>
internal static class Ui
{
    public static bool IsInteractive(IAnsiConsole console) =>
        console.Profile.Capabilities.Interactive && !Console.IsInputRedirected;

    public static void Banner(IAnsiConsole console, string subtitle)
    {
        console.WriteLine();
        console.Write(new Spectre.Console.Rule($"[bold]Tokonomy[/] [grey]·[/] {Markup.Escape(subtitle)}") { Justification = Justify.Left, Style = Style.Parse("grey") });
        console.WriteLine();
    }

    public static void Step(IAnsiConsole console, string text) => console.MarkupLine($"[blue]▸[/] {Markup.Escape(text)}");

    public static void Done(IAnsiConsole console, string text) => console.MarkupLine($"[green]✓[/] {Markup.Escape(text)}");

    public static void Note(IAnsiConsole console, string text) => console.MarkupLine($"  [grey]{Markup.Escape(text)}[/]");

    public static void Problem(IAnsiConsole console, string text) => console.MarkupLine($"[red]✗[/] {Markup.Escape(text)}");

    public static string Money(decimal usd, bool estimated) =>
        string.Create(CultureInfo.InvariantCulture, $"${usd:0.00##}") + (estimated ? " (est.)" : "");

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.Seconds}s";

    /// <summary>Runs <paramref name="work"/> behind a spinner (a plain line when the console is not interactive).</summary>
    public static async Task<T> SpinAsync<T>(IAnsiConsole console, string text, Func<Task<T>> work)
    {
        if (!IsInteractive(console))
        {
            Step(console, text);
            return await work();
        }

        var watch = Stopwatch.StartNew();
        return await console.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("blue")).StartAsync(text, async ctx =>
        {
            using var timer = new CancellationTokenSource();
            var ticker = Task.Run(async () =>
            {
                while (!timer.IsCancellationRequested)
                {
                    ctx.Status($"{Markup.Escape(text)} [grey]{Duration(watch.Elapsed)}[/]");
                    await Task.Delay(500, CancellationToken.None);
                }
            });
            try
            {
                return await work();
            }
            finally
            {
                await timer.CancelAsync();
                await ticker;
            }
        });
    }

    /// <summary>Makes the folder a usable git repository on its own (git init, first commit) and says so.</summary>
    public static async Task<string> EnsureRepoAsync(IAnsiConsole console, GitCli git, string root, CancellationToken ct)
    {
        var result = await RepoBootstrap.EnsureAsync(git, root, ct);
        if (result.InitializedRepo)
        {
            Done(console, "Created a git repository here (git init).");
        }

        if (result.CreatedInitialCommit)
        {
            Done(console, "Saved your current files as the first commit, so Tokonomy has something to branch from.");
            if (result.WroteGitIgnore)
            {
                Note(console, "Added a .gitignore (node_modules, build output, .env, ...) so those files stay out of it.");
            }
        }

        return result.Head;
    }

    /// <summary>The plan as a tree: one node per unit with what it builds and which tests will judge it.</summary>
    public static void PlanView(IAnsiConsole console, string runId, Contract contract)
    {
        var tree = new Tree($"[bold]{Markup.Escape(contract.Task)}[/]") { Style = Style.Parse("grey") };
        foreach (var unit in contract.Units)
        {
            var after = unit.DependsOn.Count > 0 ? $" [grey](after {Markup.Escape(string.Join(", ", unit.DependsOn))})[/]" : "";
            var node = tree.AddNode($"[bold blue]{Markup.Escape(unit.Id)}[/]  [bold]{Markup.Escape(unit.Title)}[/]{after}");
            node.AddNode($"[grey]{Markup.Escape(Shorten(unit.Spec, 220))}[/]");
            if (unit.Files.Count > 0)
            {
                node.AddNode($"[grey]files:[/] {Markup.Escape(string.Join(", ", unit.Files.Take(5)))}");
            }

            if (unit.Tests.Count > 0)
            {
                node.AddNode($"[green]tests (written by the architect):[/] {Markup.Escape(string.Join(", ", unit.Tests.Select(t => t.Path)))}");
            }
        }

        console.Write(new Panel(tree) { Header = new PanelHeader($" Plan {Markup.Escape(runId)} "), Border = BoxBorder.Rounded, Padding = new Padding(1, 0) });
        if (contract.Rules.Count > 0)
        {
            console.MarkupLine("[grey]Rules:[/] " + Markup.Escape(string.Join("  ·  ", contract.Rules.Select(r => r.Text))));
        }
    }

    /// <summary>End-of-run summary: verdict, per-unit result, cost, time and what to do next.</summary>
    public static void RunSummary(IAnsiConsole console, RunOutcome outcome, string runId, bool costEstimated, TimeSpan elapsed)
    {
        var passed = outcome.Status == RunStatus.Pass;
        var accepted = outcome.Units.Count(u => u.Status == "accepted");

        var table = new Table().Border(TableBorder.None).HideHeaders().AddColumn("").AddColumn("");
        foreach (var unit in outcome.Units)
        {
            var mark = unit.Status == "accepted" ? "[green]✓[/]" : "[red]✗[/]";
            var tries = unit.Attempts == 1 ? "1 attempt" : $"{unit.Attempts} attempts";
            table.AddRow(mark, $"[bold]{Markup.Escape(unit.UnitId)}[/] [grey]{tries}; {Markup.Escape(unit.Reason)}[/]");
        }

        var lines = new List<IRenderable>
        {
            new Markup(passed
                ? $"[bold green]Done.[/] {accepted} unit(s) built and verified."
                : $"[bold red]Stopped:[/] {Markup.Escape(outcome.Error ?? outcome.Status.ToString())}"),
        };
        if (outcome.Units.Count > 0)
        {
            lines.Add(table);
        }

        lines.Add(new Markup($"[grey]Cost[/]    {Markup.Escape(Money(outcome.TotalUsd, costEstimated))}   [grey]Time[/]  {Markup.Escape(Duration(elapsed))}"));
        if (outcome.Branch.Length > 0)
        {
            lines.Add(new Markup($"[grey]Branch[/]  {Markup.Escape(outcome.Branch)}"));
        }

        console.WriteLine();
        console.Write(new Panel(new Rows(lines))
        {
            Header = new PanelHeader(passed ? " Result: PASS " : " Result: FAIL "),
            BorderStyle = Style.Parse(passed ? "green" : "red"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0),
        });
    }

    public static string Shorten(string text, int max)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}

/// <summary>A spinner that can be started and stopped from callbacks, for the stretches where a worker or a gate is busy.</summary>
internal sealed class Working(IAnsiConsole console) : IDisposable
{
    private CancellationTokenSource? _stop;
    private Task? _task;
    private volatile string _text = "";

    public void Start(string text)
    {
        Stop();
        if (!Ui.IsInteractive(console))
        {
            return;
        }

        _text = text;
        var stop = _stop = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        _task = Task.Run(() => console.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("blue")).StartAsync(text, async ctx =>
        {
            while (!stop.IsCancellationRequested)
            {
                ctx.Status($"{Markup.Escape(_text)} [grey]{Ui.Duration(watch.Elapsed)}[/]");
                try
                {
                    await Task.Delay(300, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }));
    }

    public void Update(string text) => _text = text;

    public void Stop()
    {
        if (_stop is null)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            _task?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // The spinner is cosmetic; never let it break a run.
        }

        _stop.Dispose();
        _stop = null;
        _task = null;
    }

    public void Dispose() => Stop();
}
