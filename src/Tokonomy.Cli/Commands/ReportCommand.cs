using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Ledger;

namespace Tokonomy.Cli.Commands;

public sealed class ReportSettings : CommonSettings
{
    [CommandArgument(0, "[RUN_ID]")]
    [Description("Show one run in detail. Omit to list recent runs.")]
    public string? RunId { get; init; }

    [CommandOption("-n|--limit <N>")]
    [Description("Number of runs to list.")]
    [DefaultValue(20)]
    public int Limit { get; init; } = 20;
}

public sealed class ReportCommand(IAnsiConsole console) : Command<ReportSettings>
{
    public override int Execute(CommandContext context, ReportSettings settings, CancellationToken cancellationToken)
    {
        var config = ConfigLocator.LoadOrReport(console, settings, out var exit);
        if (config is null)
        {
            return exit;
        }

        using var ledger = new SqliteLedger(config.LedgerPath);
        return settings.RunId is null ? List(ledger, settings) : Detail(ledger, settings.RunId, settings.Json);
    }

    private int List(SqliteLedger ledger, ReportSettings settings)
    {
        var runs = ledger.ListRuns(Math.Max(1, settings.Limit));
        if (settings.Json)
        {
            Output.Json(console, runs);
            return ExitCodes.Ok;
        }

        if (runs.Count == 0)
        {
            console.MarkupLine("[grey]No runs recorded yet.[/]");
            return ExitCodes.Ok;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumns("Run", "Task", "Strategy", "Status", "Started (UTC)", "Cost");
        foreach (var run in runs)
        {
            table.AddRow(
                Markup.Escape(run.Id),
                Markup.Escape(run.TaskId),
                Markup.Escape(run.Strategy),
                StatusMarkup(run.Status),
                run.Started.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Money(run.TotalUsd, run.CostSource != CostSource.Metered) + (run.CostSource == CostSource.Unknown ? " [red]+ unpriced[/]" : ""));
        }

        console.Write(table);
        return ExitCodes.Ok;
    }

    /// <summary>What the architect token-share cap did in this run: the calls it skipped, or that it could not be applied.</summary>
    private void ShareCapNotes(SqliteLedger ledger, string runId)
    {
        var skipped = new List<string>();
        var warned = false;
        foreach (var payload in ledger.ListEvents(runId, Tokonomy.Runtime.Orchestration.ArchitectShare.CapEvent))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
            if (action == "warned")
            {
                warned = true;
            }
            else
            {
                skipped.Add($"{(root.TryGetProperty("purpose", out var p) ? p.GetString() : "call")} {(root.TryGetProperty("unit", out var u) ? u.GetString() : "")}".Trim());
            }
        }

        if (skipped.Count > 0)
        {
            console.MarkupLine($"[yellow]Architect token-share cap: {skipped.Count} architect call(s) skipped ({Markup.Escape(string.Join(", ", skipped))}). " +
                               "Raise budget.max_architect_token_share to let the architect continue.[/]");
        }
        else if (warned)
        {
            console.MarkupLine("[yellow]Architect token-share cap was exceeded (on_architect_token_share: warn; nothing was skipped).[/]");
        }

        if (ledger.ListEvents(runId, Tokonomy.Runtime.Orchestration.ArchitectShare.UnmeasurableEvent).Count > 0)
        {
            console.MarkupLine("[grey]Architect token-share cap not applied: some worker calls reported no token counts.[/]");
        }
    }

    private int Detail(SqliteLedger ledger, string runId, bool json)
    {
        var summary = ledger.GetSummary(runId);
        if (summary is null)
        {
            Output.Error(console, $"Run '{runId}' not found.");
            return ExitCodes.Failed;
        }

        if (json)
        {
            Output.Json(console, summary);
            return ExitCodes.Ok;
        }

        var run = summary.Run;
        console.MarkupLine($"Task: [bold]{Markup.Escape(run.TaskId)}[/]   Strategy: {Markup.Escape(run.Strategy)}   Status: {StatusMarkup(run.Status)}");

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Tier", "Calls", "Input tok", "Output tok", "Cost");
        foreach (var tier in summary.Tiers)
        {
            table.AddRow(
                tier.Role.ToString().ToLowerInvariant(),
                tier.Calls.ToString(CultureInfo.InvariantCulture),
                tier.InputTokens.ToString("N0", CultureInfo.InvariantCulture),
                tier.OutputTokens.ToString("N0", CultureInfo.InvariantCulture),
                Money(tier.Usd, tier.AnyEstimated));
        }

        console.Write(table);
        var total = summary.Tiers.Sum(t => t.Usd);
        var totalTokens = summary.Tiers.Sum(t => t.InputTokens + t.OutputTokens);
        var architectTokens = summary.Tiers.Where(t => t.Role == LlmRole.Architect).Sum(t => t.InputTokens + t.OutputTokens);
        console.MarkupLine($"Total: [bold]{Money(total, summary.Tiers.Any(t => t.AnyEstimated))}[/]" +
                           (totalTokens > 0 ? $"   Architect token share: {(double)architectTokens / totalTokens:P0}" : ""));
        if (summary.Run.CostSource == CostSource.Unknown)
        {
            console.MarkupLine($"[red]{Markup.Escape(Ui.UnpricedNote)}[/]");
        }

        ShareCapNotes(ledger, runId);

        var architectPurposes = summary.Purposes.Where(p => p.Role == LlmRole.Architect).ToList();
        if (architectPurposes.Count > 1 || architectPurposes.Any(p => p.Purpose != "unspecified"))
        {
            console.MarkupLine("Architect spend by purpose: " + string.Join("  ·  ", architectPurposes.Select(p =>
                $"{Markup.Escape(p.Purpose)} {p.Calls}× {Money(p.Usd, p.AnyEstimated)}")));
        }

        var s = summary.Stats;
        if (s.Units > 0)
        {
            console.MarkupLine(
                $"Units: {s.UnitsAccepted}/{s.Units} accepted ({s.UnitsAcceptedWithoutTakeover} without takeover) · worker attempts {s.WorkerAttempts} (retries {s.WorkerRetries}) · " +
                $"takeovers {s.Takeovers} · interventions {s.Interventions} · plan revisions {s.PlanRevisions}" +
                (s.ReviewChangeRequests > 0 ? $" · review change requests {s.ReviewChangeRequests}" : "") +
                (s.RepeatedMistakes > 0 ? $" · [yellow]repeated mistakes {s.RepeatedMistakes}[/]" : ""));
        }

        if (summary.Interventions.Count > 0)
        {
            var interventions = new Table().Border(TableBorder.Rounded).AddColumns("Unit", "Trigger", "Level", "Outcome", "Found / did", "Rev");
            foreach (var i in summary.Interventions)
            {
                interventions.AddRow(
                    Markup.Escape(i.UnitId),
                    Markup.Escape(i.Trigger),
                    i.Level < 0 ? "-" : i.Level.ToString(CultureInfo.InvariantCulture),
                    Markup.Escape(i.Outcome),
                    Markup.Escape(Shorten($"{i.Diagnosis} → {i.Action}", 140)),
                    i.PlanRevision?.ToString(CultureInfo.InvariantCulture) ?? "");
            }

            console.MarkupLine("[bold]Architect interventions[/]");
            console.Write(interventions);
        }

        if (summary.Revisions.Count > 0)
        {
            console.MarkupLine("[bold]Plan revisions[/] (full before/after values: .tokonomy/runs/" + Markup.Escape(runId) + "/plan-state.json)");
            foreach (var r in summary.Revisions)
            {
                console.MarkupLine($"  rev {r.Revision} [grey]{Markup.Escape(r.Source)}, {r.Changes} change(s){(r.Trigger is null ? "" : ", " + Markup.Escape(r.Trigger))}:[/] {Markup.Escape(Shorten(r.Reason, 140))}" +
                                   (r.RequiresApproval ? " [yellow](human approval)[/]" : ""));
            }
        }

        foreach (var review in summary.Reviews)
        {
            console.MarkupLine($"Review {Markup.Escape(review.UnitId)}: [bold]{Markup.Escape(review.Verdict.ToString())}[/] [grey]({Markup.Escape(review.Why)})[/] {Markup.Escape(Shorten(review.Summary, 140))}");
            foreach (var finding in review.Findings.Take(5))
            {
                console.MarkupLine($"  - {Markup.Escape(Shorten(finding.ToString(), 180))}");
            }
        }

        return ExitCodes.Ok;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    private static string Money(decimal usd, bool estimated) =>
        "$" + usd.ToString("0.0000", CultureInfo.InvariantCulture) + (estimated ? " [yellow](est.)[/]" : "");

    private static string StatusMarkup(RunStatus status) => status switch
    {
        RunStatus.Pass => "[green]PASS[/]",
        RunStatus.Fail => "[red]FAIL[/]",
        RunStatus.AbortedBudget => "[red]ABORTED (budget)[/]",
        RunStatus.Error => "[red]ERROR[/]",
        _ => "[blue]RUNNING[/]",
    };
}
