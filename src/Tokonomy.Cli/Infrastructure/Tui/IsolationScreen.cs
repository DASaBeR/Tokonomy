using System.Text.RegularExpressions;
using Spectre.Console;
using Tokonomy.Runtime.Config;

namespace Tokonomy.Cli.Infrastructure.Tui;

/// <summary>Shows where gates run and lets the user switch between Windows Sandbox and this computer (policy.yaml, gate_execution.provider).</summary>
internal static partial class IsolationScreen
{
    private const string Sandbox = "windows-sandbox";
    private const string Host = "host-unsafe";

    [GeneratedRegex(@"^(\s*provider:\s*)(windows-sandbox|host-unsafe)(.*)$", RegexOptions.Multiline)]
    private static partial Regex ProviderLine();

    public static bool SandboxInstalled() =>
        OperatingSystem.IsWindows() &&
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsSandbox.exe"));

    /// <summary>Returns the new provider, or null when nothing changed.</summary>
    public static string? Run(IAnsiConsole console, string? configDirectory = null)
    {
        var dir = configDirectory ?? ConfigLoader.FindConfigDirectory(Directory.GetCurrentDirectory());
        var file = dir is null ? null : Path.Combine(dir, "policy.yaml");
        if (file is null || !File.Exists(file))
        {
            Output.Error(console, "No .tokonomy/policy.yaml found here. Run Setup first.");
            return null;
        }

        var text = File.ReadAllText(file);
        var gate = text.IndexOf("gate_execution:", StringComparison.Ordinal);
        var match = gate < 0 ? null : ProviderLine().Match(text, gate);
        var current = match is { Success: true } ? match.Groups[2].Value : null;

        console.MarkupLine("[bold]Where should the tests run?[/]");
        console.MarkupLine("While building, the cheap model writes code and Tokonomy then runs that code's tests (the \"gates\").");
        console.MarkupLine($"  Now:             [aqua]{Markup.Escape(current ?? "not chosen yet")}[/]");
        console.MarkupLine(SandboxInstalled()
            ? "  Windows Sandbox: [green]✓ available[/]"
            : "  Windows Sandbox: [yellow]not available[/] [grey](needs Windows Pro/Enterprise/Education with the feature switched on)[/]");
        console.WriteLine();

        const string safe = "Windows Sandbox  (safe: tests run in a throwaway VM)";
        const string unsafeHost = "This computer  (less safe: tests run here with your privileges)";
        var pick = console.Prompt(new SelectionPrompt<string>()
            .Title("Choose")
            .HighlightStyle(Theme.Accent)
            .AddChoices(safe, unsafeHost, "‹ Back"));
        if (pick == "‹ Back")
        {
            return null;
        }

        var target = pick == safe ? Sandbox : Host;
        if (target == current)
        {
            Output.Warn(console, "Already set to that.");
            return null;
        }

        if (target == Sandbox && !SandboxInstalled())
        {
            Output.Warn(console, "Windows Sandbox is not available on this computer, so a build will stop until you enable it. Windows features → Windows Sandbox, then restart.");
        }

        if (target == Host)
        {
            console.MarkupLine("[yellow]![/] Code the model writes will run on this computer, with your privileges. Use it for trusted tasks and your own projects only.");
            if (!console.Confirm("Run the tests on this computer?", defaultValue: false))
            {
                return null;
            }
        }

        string updated;
        if (match is { Success: true })
        {
            updated = ProviderLine().Replace(text, m => m.Groups[1].Value + target + m.Groups[3].Value, 1, gate);
        }
        else
        {
            updated = text.TrimEnd() + $"{Environment.NewLine}{Environment.NewLine}gate_execution:{Environment.NewLine}  provider: {target}{Environment.NewLine}";
        }

        File.WriteAllText(file, updated);
        Ui.Done(console, $"Tests will now run in: {target}");
        return target;
    }
}
