using Spectre.Console;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Orchestration;

namespace Tokonomy.Cli.Infrastructure;

/// <summary>Live progress for `run` and `build`: a header per unit, a spinner while the worker or the gates work, one result line per attempt.</summary>
internal sealed class ConsoleObserver(IAnsiConsole console, Contract? contract = null) : IRunObserver, IDisposable
{
    private readonly Working _working = new(console);
    private long _tokens;
    private decimal _spent;
    private bool _any;

    private string Live => _any ? $" · {Ui.Money(_spent, false)} · {Tokens(_tokens)} tokens" : "";

    private static string Tokens(long n) => n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void Usage(LlmRole role, string model, long inputTokens, long outputTokens, decimal usd, decimal totalUsd)
    {
        _tokens += inputTokens + outputTokens;
        _spent = totalUsd;
        _any = true;
        _working.Extra = () => Live;
    }

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
        console.MarkupLine($"  [{color}]{mark}[/] attempt {attempt}: {how}{gateText}  [{color}]{verdict}[/]{(_any ? $"  [grey]{Markup.Escape(Live.TrimStart(' ', '·'))}[/]" : "")}");
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

    public void WorkerReported(WorkUnit unit, Tokonomy.Core.Supervision.WorkerReport report)
    {
        if (report.Status == Tokonomy.Core.Supervision.WorkerReportStatus.Completed && !report.DecisionRequired && report.Discoveries.Count == 0)
        {
            return;
        }

        _working.Stop();
        console.MarkupLine($"    [blue]worker reports[/] [grey]{Markup.Escape(Ui.Shorten(report.Summary, 200))}[/]");
    }

    public void SupervisionStarted(WorkUnit unit, Tokonomy.Core.Supervision.SupervisionRequest request)
    {
        _working.Stop();
        console.MarkupLine($"  [blue]» architect supervising {Markup.Escape(unit.Id)}[/] [grey]({Markup.Escape(RunOrchestrator.TriggerName(request.Trigger))}: {Markup.Escape(Ui.Shorten(request.Reason, 160))})[/]");
        _working.Start($"{unit.Id}: the architect is diagnosing");
    }

    public void InterventionFinished(WorkUnit unit, InterventionRecord intervention)
    {
        _working.Stop();
        var level = intervention.Level switch
        {
            0 when intervention.Outcome == "split" => "split",
            0 => "no action",
            1 => "L1 instruction correction",
            2 => "L2 guided correction",
            3 => "L3 takeover",
            _ => "no usable decision",
        };
        var color = intervention.Outcome switch
        {
            "accepted" or "resumed" or "split" => "blue",
            "human" or "failed" => "red",
            _ => "grey",
        };
        console.MarkupLine($"    [{color}]{Markup.Escape(level)} → {Markup.Escape(intervention.Outcome)}[/]" +
                           (intervention.PlanRevision is { } r ? $" [grey](plan revision {r})[/]" : ""));
        if (intervention.Diagnosis.Length > 0)
        {
            console.MarkupLine($"    [grey]found: {Markup.Escape(Ui.Shorten(intervention.Diagnosis, 220))}[/]");
        }

        if (intervention.Action.Length > 0)
        {
            console.MarkupLine($"    [grey]did: {Markup.Escape(Ui.Shorten(intervention.Action, 220))}[/]");
        }
    }

    public void PlanUpdated(Tokonomy.Core.Planning.PlanRevision revision)
    {
        _working.Stop();
        var units = revision.Changes.Select(c => c.UnitId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        console.MarkupLine($"    [blue]plan updated → revision {revision.Number}[/] [grey]({revision.Changes.Count} change(s){(units.Count > 0 ? " to " + Markup.Escape(string.Join(", ", units.Take(6))) : "")}; {Markup.Escape(revision.Source)})[/]");
    }

    public void ReviewFinished(WorkUnit unit, UnitReview review, string why)
    {
        _working.Stop();
        var color = review.Verdict switch
        {
            UnitReviewVerdict.Approve => "green",
            UnitReviewVerdict.ApproveWithWarnings or UnitReviewVerdict.Unavailable => "yellow",
            _ => "red",
        };
        console.MarkupLine($"  [{color}]architect review: {Markup.Escape(review.Verdict.ToString())}[/] [grey]{Markup.Escape(Ui.Shorten(review.Summary, 160))}[/]");
        foreach (var finding in review.Findings.Take(3))
        {
            console.MarkupLine($"    [grey]- {Markup.Escape(Ui.Shorten(finding.ToString(), 200))}[/]");
        }
    }

    public void Dispose() => _working.Dispose();
}
