using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Net;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Cli.Commands;

public sealed class LoginSettings : CommandSettings
{
    [CommandArgument(0, "[TOOL]")]
    [Description("claude, codex or antigravity. Without it: show who is signed in and offer to sign in the ones that are not.")]
    public string? Tool { get; init; }

    [CommandOption("--device-code")]
    [Description("Codex only: sign in with a device code instead of the browser (for a machine without one).")]
    public bool DeviceCode { get; init; }
}

/// <summary><c>tokonomy login</c>: sign a coding-agent CLI in to its provider (or fix an expired login) without leaving Tokonomy.</summary>
public sealed class LoginCommand(IAnsiConsole console, IProcessRunner runner) : AsyncCommand<LoginSettings>
{
    private static readonly (string Name, ConnectionKind Kind, string Title)[] Tools =
    [
        ("claude", ConnectionKind.ClaudeCode, "Claude Code"),
        ("codex", ConnectionKind.Codex, "Codex (OpenAI)"),
        ("antigravity", ConnectionKind.Antigravity, "Antigravity (Google)"),
    ];

    public override async Task<int> ExecuteAsync(CommandContext context, LoginSettings settings, CancellationToken cancellationToken)
    {
        var cwd = Directory.GetCurrentDirectory();
        if (ProxySettings.Current.IsActive)
        {
            Ui.Note(console, $"Proxy on: {ProxySettings.Current.Display}. Sign-in and checks go through it.");
        }

        if (settings.Tool is not null)
        {
            var wanted = settings.Tool.Trim().ToLowerInvariant();
            wanted = wanted is "agy" ? "antigravity" : wanted is "claude-code" ? "claude" : wanted;
            var match = Tools.FirstOrDefault(t => t.Name == wanted);
            if (match.Name is null)
            {
                Output.Error(console, $"Unknown tool '{settings.Tool}'. Use claude, codex or antigravity.");
                return ExitCodes.Usage;
            }

            return await SignInAsync(match.Kind, match.Title, settings.DeviceCode, cwd, cancellationToken);
        }

        var states = new List<(string Name, ConnectionKind Kind, string Title, ToolState State)>();
        foreach (var (name, kind, title) in Tools)
        {
            var (state, _) = await Ui.SpinAsync(console, $"Checking {title}", () => MachineScan.CheckToolAsync(kind, runner, cwd, cancellationToken));
            states.Add((name, kind, title, state));
            if (state.Ready && state.SignedIn == true)
            {
                Ui.Done(console, $"{title}: {state.Detail}");
            }
            else if (state.Installed)
            {
                Ui.Problem(console, $"{title}: {state.Detail}");
            }
            else
            {
                console.MarkupLine($"[grey]- {Markup.Escape(title)}: not installed ({Markup.Escape(state.InstallHint)})[/]");
            }
        }

        var needLogin = states.Where(s => s.State is { Installed: true, SignedIn: false }).ToList();
        if (needLogin.Count == 0 || !Ui.IsInteractive(console))
        {
            if (needLogin.Count > 0)
            {
                Ui.Note(console, "Sign in with: " + string.Join(", ", needLogin.Select(s => $"tokonomy login {s.Name}")));
            }

            return needLogin.Count == 0 ? ExitCodes.Ok : ExitCodes.Failed;
        }

        var pick = console.Prompt(new SelectionPrompt<string>()
            .Title("Sign in to which one?")
            .AddChoices(needLogin.Select(s => s.Title).Append("None, leave it")));
        var chosen = needLogin.FirstOrDefault(s => s.Title == pick);
        return chosen.Name is null ? ExitCodes.Ok : await SignInAsync(chosen.Kind, chosen.Title, settings.DeviceCode, cwd, cancellationToken);
    }

    private async Task<int> SignInAsync(ConnectionKind kind, string title, bool deviceCode, string cwd, CancellationToken ct)
    {
        var plan = ToolLogin.PlanFor(kind, deviceCode && kind == ConnectionKind.Codex)!;
        console.MarkupLine($"[bold]{Markup.Escape(plan.Title)}[/]");
        Ui.Note(console, plan.Before);
        console.WriteLine();
        try
        {
            await ToolLogin.RunAsync(plan, cwd, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ExecutableNotFoundException or Win32Exception)
        {
            Ui.Problem(console, $"Could not start {title}: {Ui.Shorten(ex.Message, 160)}");
            var (check, _) = await MachineScan.CheckToolAsync(kind, runner, cwd, ct);
            if (!check.Installed)
            {
                Ui.Note(console, "To install it: " + check.InstallHint);
            }

            return ExitCodes.Failed;
        }

        console.WriteLine();
        var (state, _) = await Ui.SpinAsync(console, "Checking the sign-in", () => MachineScan.CheckToolAsync(kind, runner, cwd, ct));
        if (state is { Installed: true, SignedIn: not false })
        {
            Ui.Done(console, $"{title}: {state.Detail}");
            return ExitCodes.Ok;
        }

        Ui.Problem(console, $"{title}: still not signed in ({state.Detail}).");
        return ExitCodes.Failed;
    }
}
