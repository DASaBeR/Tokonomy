using Spectre.Console.Testing;
using Tokonomy.Cli.Infrastructure.Tui;

namespace Tokonomy.E2E.Tests;

public class IsolationScreenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tokonomy-iso-" + Guid.NewGuid().ToString("N"));

    public IsolationScreenTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WritePolicy(string gateExecution)
    {
        File.WriteAllText(Path.Combine(_dir, "policy.yaml"), "models:\n  architect: a\n\n" + gateExecution);
        return _dir;
    }

    private static TestConsole NewConsole() => new TestConsole().Interactive();

    [Fact]
    public void Switching_to_this_computer_needs_confirmation_and_rewrites_only_the_provider_line()
    {
        WritePolicy("gate_execution:\n  provider: windows-sandbox     # windows-sandbox | host-unsafe\n  network_policy: none\n");
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.DownArrow); // "This computer"
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("y");

        var result = IsolationScreen.Run(console, _dir);

        Assert.Equal("host-unsafe", result);
        var text = File.ReadAllText(Path.Combine(_dir, "policy.yaml"));
        Assert.Contains("  provider: host-unsafe     # windows-sandbox | host-unsafe", text);
        Assert.Contains("network_policy: none", text);
        Assert.Contains("architect: a", text);
    }

    [Fact]
    public void Declining_the_confirmation_changes_nothing()
    {
        WritePolicy("gate_execution:\n  provider: windows-sandbox\n");
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("n");

        Assert.Null(IsolationScreen.Run(console, _dir));
        Assert.Contains("provider: windows-sandbox", File.ReadAllText(Path.Combine(_dir, "policy.yaml")));
    }

    [Fact]
    public void A_policy_without_gate_execution_gets_the_section_added()
    {
        WritePolicy("");
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("y");

        Assert.Equal("host-unsafe", IsolationScreen.Run(console, _dir));
        Assert.Contains("gate_execution:", File.ReadAllText(Path.Combine(_dir, "policy.yaml")));
    }
}
