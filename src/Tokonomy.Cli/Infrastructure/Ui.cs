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

    /// <summary>Throws away keys typed or pasted ahead of time, so they cannot answer the next prompt by accident.</summary>
    public static void DiscardPendingInput()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        try
        {
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Reads the task. A multi-line paste arrives as several lines at once; a plain prompt would take the first and leave the
    /// rest to answer the next questions. Everything pasted together is kept as one task.
    /// </summary>
    public static string ReadTask(IAnsiConsole console, string prompt, bool allowEmpty = false)
    {
        var textPrompt = new TextPrompt<string>(prompt);
        if (allowEmpty)
        {
            textPrompt.AllowEmpty();
        }

        var lines = new List<string> { console.Prompt(textPrompt) };
        if (!Console.IsInputRedirected)
        {
            try
            {
                Thread.Sleep(100); // let the rest of a paste arrive
                var line = new System.Text.StringBuilder();
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                    }
                    else if (!char.IsControl(key.KeyChar) || key.KeyChar == '\t')
                    {
                        line.Append(key.KeyChar);
                    }

                    if (!Console.KeyAvailable)
                    {
                        Thread.Sleep(50);
                    }
                }

                if (line.Length > 0)
                {
                    lines.Add(line.ToString());
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        var task = string.Join("\n", lines.Select(l => l.TrimEnd())).Trim();
        if (lines.Count > 1 && task.Length > 0)
        {
            Note(console, $"Took all {lines.Count} pasted lines as the task.");
        }

        return task;
    }

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

    /// <summary>
    /// Runs <paramref name="work"/> behind a spinner (a plain line when the console is not interactive). <paramref name="detail"/>
    /// is read every half second and shown after the elapsed time, for live progress such as tokens written so far.
    /// </summary>
    public static async Task<T> SpinAsync<T>(IAnsiConsole console, string text, Func<Task<T>> work, Func<string?>? detail = null)
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
                    var extra = detail?.Invoke();
                    ctx.Status($"{Markup.Escape(text)} [grey]{Duration(watch.Elapsed)}{(string.IsNullOrEmpty(extra) ? "" : " · " + Markup.Escape(extra))}[/]");
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

    /// <summary>
    /// The plan: what the architect found and decided (for a blueprint), then one node per unit with what it builds, its
    /// risk, its dependencies and which tests will judge it.
    /// </summary>
    public static void PlanView(IAnsiConsole console, string runId, Contract contract)
    {
        if (contract.Analysis is { } analysis)
        {
            var found = analysis.Facts.Count > 0 ? $"{analysis.Facts.Count} verified fact(s)" : "no verified facts";
            console.MarkupLine($"[grey]Discovered:[/] {found}, {analysis.Requirements.Count} requirement(s), {analysis.Assumptions.Count} assumption(s)");
            foreach (var fact in analysis.Facts.Take(4))
            {
                console.MarkupLine($"  [grey]•[/] {Markup.Escape(Shorten(fact.Text, 140))} [grey]({Markup.Escape(string.Join(", ", fact.Evidence.Take(2)))})[/]");
            }

            foreach (var assumption in analysis.Assumptions.Take(3))
            {
                console.MarkupLine($"  [yellow]?[/] {Markup.Escape(Shorten(assumption, 140))} [grey](assumed)[/]");
            }
        }

        if (contract.Architecture is { Summary.Length: > 0 } architecture)
        {
            console.MarkupLine($"[grey]Architecture:[/] {Markup.Escape(Shorten(architecture.Summary, 300))}");
        }

        foreach (var decision in contract.Decisions.Take(6))
        {
            console.MarkupLine($"  [blue]{Markup.Escape(decision.Id)}[/] {Markup.Escape(Shorten(decision.Title, 60))}: {Markup.Escape(Shorten(decision.Decision, 140))}");
        }

        var tree = new Tree($"[bold]{Markup.Escape(contract.Task)}[/]") { Style = Style.Parse("grey") };
        foreach (var unit in contract.Units)
        {
            var after = unit.DependsOn.Count > 0 ? $" [grey](after {Markup.Escape(string.Join(", ", unit.DependsOn))})[/]" : "";
            var risk = !contract.IsBlueprint ? "" : unit.Risk switch
            {
                UnitRisk.High => "  [red]high risk[/]",
                UnitRisk.Medium => "  [yellow]medium risk[/]",
                _ => "",
            };
            var node = tree.AddNode($"[bold blue]{Markup.Escape(unit.Id)}[/]  [bold]{Markup.Escape(unit.Title)}[/]{after}{risk}");
            node.AddNode($"[grey]{Markup.Escape(Shorten(unit.Objective ?? unit.Spec, 220))}[/]");
            if (unit.ExpectedFiles.Count > 0)
            {
                node.AddNode($"[grey]files:[/] {Markup.Escape(string.Join(", ", unit.ExpectedFiles.Take(5)))}");
            }

            if (unit.Steps.Count > 0)
            {
                node.AddNode($"[grey]{unit.Steps.Count} step(s), {unit.Acceptance.Count} acceptance criteria{(unit.Requirements.Count > 0 ? ", covers " + Markup.Escape(string.Join(", ", unit.Requirements)) : "")}[/]");
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

        if (contract.Analysis is { Risks.Count: > 0 } withRisks)
        {
            console.MarkupLine("[grey]Risks:[/] " + Markup.Escape(string.Join("  ·  ", withRisks.Risks.Take(4).Select(r => Shorten(r, 120)))));
        }
    }

    /// <summary>The plan's quality verdict, its critique and the issues still open.</summary>
    public static void PlanQualityView(IAnsiConsole console, PlannedTask planned)
    {
        if (!planned.Contract.IsBlueprint && planned.Critique is null && planned.Assessment.Issues.Count == 0)
        {
            return;
        }

        var assessment = planned.Assessment;
        var color = assessment.IsReady ? "green" : "yellow";
        var revised = planned.RevisionRounds > 0 ? $", revised {planned.RevisionRounds} time(s)" : "";
        var critique = planned.Critique is { } c ? $"; critique: {c.Verdict.ToString().ToLowerInvariant()}{(c.Summary.Length > 0 ? " - " + Shorten(c.Summary, 120) : "")}" : "";
        var inspected = planned.InspectedFiles.Count > 0 ? $"; read {planned.InspectedFiles.Count} file(s) first" : "";
        console.MarkupLine($"[grey]Plan check:[/] [{color}]{Readiness(assessment.Readiness)}[/]{Markup.Escape(revised + critique + inspected)}");
        foreach (var issue in assessment.Issues.OrderByDescending(i => i.Blocking).Take(6))
        {
            var mark = issue.Blocking ? "[yellow]![/]" : "[grey]·[/]";
            console.MarkupLine($"  {mark} {Markup.Escape(Shorten(issue.ToString(), 180))}");
        }
    }

    public static string Readiness(Tokonomy.Core.Planning.PlanReadiness readiness) => readiness switch
    {
        Tokonomy.Core.Planning.PlanReadiness.Ready => "ready",
        Tokonomy.Core.Planning.PlanReadiness.Incomplete => "incomplete",
        Tokonomy.Core.Planning.PlanReadiness.ArchitecturallyInconsistent => "architecturally inconsistent",
        _ => "structurally invalid",
    };

    /// <summary>End-of-run summary: verdict, per-unit result, cost, time and what to do next.</summary>
    public static void RunSummary(IAnsiConsole console, RunOutcome outcome, string runId, bool costEstimated, TimeSpan elapsed)
    {
        var passed = outcome.Status == RunStatus.Pass;
        var accepted = outcome.Units.Count(u => u.Status == "accepted");

        var table = new Table().Border(TableBorder.None).HideHeaders().AddColumn("").AddColumn("");
        foreach (var unit in outcome.Units)
        {
            var mark = unit.Status switch
            {
                UnitOutcome.Accepted => "[green]✓[/]",
                UnitOutcome.Split => "[blue]↳[/]",
                _ => "[red]✗[/]",
            };
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

    /// <summary>Extra live text shown after the elapsed time, e.g. the running cost.</summary>
    public Func<string>? Extra { get; set; }

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
                ctx.Status($"{Markup.Escape(_text)} [grey]{Ui.Duration(watch.Elapsed)}{Markup.Escape(Extra?.Invoke() ?? "")}[/]");
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
