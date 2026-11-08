using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Taskrail.Cli.Infrastructure;
using Taskrail.Core.Model;
using Taskrail.Runtime.Ledger;

namespace Taskrail.Cli.Commands;

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
                Money(run.TotalUsd, run.CostSource == CostSource.Estimated));
        }

        console.Write(table);
        return ExitCodes.Ok;
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
        return ExitCodes.Ok;
    }

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
