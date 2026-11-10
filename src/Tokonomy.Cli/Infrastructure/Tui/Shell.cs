using System.Reflection;
using System.Globalization;
using Spectre.Console;
using Tokonomy.Cli.Commands;
using Tokonomy.Runtime.Ledger;

namespace Tokonomy.Cli.Infrastructure.Tui;

/// <summary>
/// The interactive home screen: a banner and an arrow-key menu that reaches every section.
/// It is a thin front end: each entry runs the matching command through <paramref name="runCommand"/>,
/// so the shell and the classic command line share one code path.
/// </summary>
internal sealed class Shell(IAnsiConsole console, Func<string[], Task<int>> runCommand)
{
    private sealed record Item(string Title, string Description, Func<Task<string[]?>> Args);

    public async Task<int> RunAsync()
    {
        var items = BuildItems();
        var quit = new Item("Quit", "", () => Task.FromResult<string[]?>(null));
        while (true)
        {
            Theme.Banner(console, GetVersion());

            var choice = console.Prompt(
                new SelectionPrompt<Item>()
                    .Title("[bold]What would you like to do?[/]  [grey](↑/↓ to move, Enter to select)[/]")
                    .PageSize(12)
                    .HighlightStyle(Theme.Accent)
                    .AddChoices(items.Append(quit))
                    .UseConverter(i => ReferenceEquals(i, quit)
                        ? "[grey]Quit[/]"
                        : $"{Markup.Escape(i.Title)}  [grey]{Markup.Escape(i.Description)}[/]"));

            if (ReferenceEquals(choice, quit))
            {
                console.MarkupLine("[grey]Bye.[/]");
                return ExitCodes.Ok;
            }

            console.Clear();
            _backed = false;
            try
            {
                var args = await choice.Args();
                if (args is not null)
                {
                    console.WriteLine();
                    await runCommand(args);
                }
            }
            catch (OperationCanceledException)
            {
                Output.Warn(console, "Cancelled.");
            }
            catch (Exception ex)
            {
                Output.Error(console, ex.Message);
            }

            if (!_backed)
            {
                console.WriteLine();
                console.MarkupLine("[grey]Press any key to go back to the menu...[/]");
                Console.ReadKey(intercept: true);
            }

            console.Clear();
        }
    }

    private List<Item> BuildItems() =>
    [
        new("Build a task", "set up, plan, confirm, build, verify, merge", () => AskTask("build")),
        new("Plan only", "see the plan first, run it later", () => AskTask("plan")),
        new("Run the latest plan", "execute the plan you made", () => Task.FromResult<string[]?>(["run"])),
        new("Runs and costs", "browse past runs and what they cost", PickRun),
        new("Doctor", "check git, tools, API keys, gates", AskDoctor),
        new("Setup", "choose who plans and who codes", () => Task.FromResult<string[]?>(["init"])),
        new("Accounts", "sign in to Claude, Codex or Antigravity", AskAccount),
        new("Proxy", "route connections through HTTP or SOCKS5", AskProxy),
        new("Benchmark", "compare strategies on a taskset", AskBench),
        new("Help", "what the words mean", () => { Help(); return Task.FromResult<string[]?>(null); }),
    ];

    private const string Back = "‹ Back";
    private bool _backed;

    /// <summary>A small arrow-key submenu. Returns the chosen value, or null for Back.</summary>
    private T? Choose<T>(string title, IEnumerable<(string Label, T Value)> options) where T : class
    {
        var list = options.ToList();
        var picked = console.Prompt(new SelectionPrompt<string>()
            .Title($"[bold]{Markup.Escape(title)}[/]")
            .PageSize(12)
            .HighlightStyle(Theme.Accent)
            .AddChoices(list.Select(o => o.Label).Append(Back)));
        _backed = picked == Back;
        return picked == Back ? null : list.First(o => o.Label == picked).Value;
    }

    private Task<string[]?> AskDoctor() =>
        Task.FromResult(Choose<string[]>("Doctor", [
            ("Check everything", ["doctor"]),
            ("Check and remove leftover worktrees", ["doctor", "--remove-leftover-worktrees"]),
            ("Check and remove leftover gate sandboxes", ["doctor", "--remove-leftover-gate-environments"]),
        ]));

    private Task<string[]?> AskAccount() =>
        Task.FromResult(Choose<string[]>("Accounts", [
            ("Show sign-in status", ["login"]),
            ("Sign in to Claude", ["login", "claude"]),
            ("Sign in to Codex (browser)", ["login", "codex"]),
            ("Sign in to Codex (device code)", ["login", "codex", "--device-code"]),
            ("Sign in to Antigravity", ["login", "antigravity"]),
        ]));

    private Task<string[]?> AskProxy()
    {
        var choice = Choose<string[]>("Proxy", [
            ("Show current proxy", ["proxy"]),
            ("Set a proxy address", ["proxy", "set"]),
            ("Choose which connections use it", ["proxy", "scope"]),
            ("Turn on", ["proxy", "on"]),
            ("Turn off", ["proxy", "off"]),
            ("Test connections", ["proxy", "test"]),
        ]);
        if (choice is ["proxy", "set"])
        {
            var addr = console.Prompt(new TextPrompt<string>("[bold]Proxy address[/] [grey](e.g. 127.0.0.1:10808):[/]").AllowEmpty());
            return Task.FromResult<string[]?>(string.IsNullOrWhiteSpace(addr) ? null : ["proxy", "set", addr.Trim()]);
        }

        return Task.FromResult(choice);
    }

    private Task<string[]?> PickRun()
    {
        var config = ConfigLocator.LoadOrReport(console, new ReportSettings(), out _);
        if (config is null)
        {
            return Task.FromResult<string[]?>(null);
        }

        using var ledger = new SqliteLedger(config.LedgerPath);
        var runs = ledger.ListRuns(20);
        if (runs.Count == 0)
        {
            console.MarkupLine("[grey]No runs recorded yet. Choose Build a task to make one.[/]");
            return Task.FromResult<string[]?>(null);
        }

        var choice = Choose<string[]>("Pick a run", runs.Select(r => (
            Label: $"{r.Id}  {r.Started.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}  {r.Status}  {Ui.Money(r.TotalUsd, r.CostSource == Tokonomy.Core.Model.CostSource.Estimated)}  {Truncate(r.TaskId, 40)}",
            Value: new[] { "report", r.Id })));
        return Task.FromResult(choice);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private Task<string[]?> AskTask(string command)
    {
        var task = console.Prompt(new TextPrompt<string>("[bold]Describe the task:[/]").AllowEmpty());
        return Task.FromResult<string[]?>(string.IsNullOrWhiteSpace(task) ? null : [command, task]);
    }

    private Task<string[]?> AskBench()
    {
        var file = console.Prompt(new TextPrompt<string>("[bold]Path to the taskset file:[/]").AllowEmpty());
        return Task.FromResult<string[]?>(string.IsNullOrWhiteSpace(file) ? null : ["bench", file]);
    }

    private void Help()
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).HideHeaders()
            .AddColumn("term").AddColumn("meaning");
        table.AddRow("[bold]Plan[/]", "the strong model splits your task into small units");
        table.AddRow("[bold]Unit[/]", "one small piece of work done by the cheap model");
        table.AddRow("[bold]Gate[/]", "a check (tests, lint, build) a unit must pass");
        table.AddRow("[bold]Retry[/]", "a failed unit is tried again, then the strong model takes over");
        table.AddRow("[bold]Ledger[/]", "the record of tokens and cost for every run");
        table.AddRow("[bold]Takeover[/]", "the strong model finishes a unit the cheap one could not");
        table.AddRow("[bold]Strategy[/]", "how work is split between strong and cheap models");
        table.AddRow("[bold]Isolation[/]", "gates run inside Windows Sandbox so they cannot touch your machine");
        console.Write(table);
        Output.Warn(console, "Full glossary: docs/glossary.md");
    }

    private static string GetVersion() =>
        typeof(Shell).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
}
