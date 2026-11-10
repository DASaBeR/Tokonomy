using Spectre.Console.Testing;
using TuiShell = Tokonomy.Cli.Infrastructure.Tui.Shell;

namespace Tokonomy.E2E.Tests;

public class ShellTests
{
    private static TestConsole NewConsole()
    {
        var console = new TestConsole().Interactive();
        console.Profile.Width = 120;
        console.EmitAnsiSequences = false;
        return console;
    }

    [Fact]
    public async Task Quit_is_the_last_entry_and_exits_cleanly()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.UpArrow); // wraps from the first entry to Quit
        console.Input.PushKey(ConsoleKey.Enter);

        var exit = await new TuiShell(console, _ => Task.FromResult(0)).RunAsync();

        Assert.Equal(0, exit);
        Assert.Contains("Tokonomy", console.Output);
        Assert.Contains("What would you like to do?", console.Output);
    }

    [Fact]
    public async Task Plain_text_in_the_free_prompt_is_built_as_a_task()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Enter); // first entry: "Type a task or /command"
        console.Input.PushTextWithEnter("add a login page");
        console.Input.PushKey(ConsoleKey.Enter); // "press any key"
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter); // Quit
        string[]? seen = null;

        await new TuiShell(console, a => { seen = a; return Task.FromResult(0); }).RunAsync();

        Assert.NotNull(seen);
        Assert.Equal(["build", "add a login page"], seen!);
    }

    [Fact]
    public async Task A_slash_command_runs_the_matching_command()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("/doctor");
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        string[]? seen = null;

        await new TuiShell(console, a => { seen = a; return Task.FromResult(0); }).RunAsync();

        Assert.NotNull(seen);
        Assert.Equal(["doctor"], seen!);
    }
}
