using System.Diagnostics;
using Tokonomy.Core.Net;
using Tokonomy.Runtime.Config;

namespace Tokonomy.Runtime.Process;

/// <summary>How to sign a coding-agent CLI in to its provider: the command, and what to tell the user around it.</summary>
public sealed record LoginPlan(ConnectionKind Kind, string Title, IReadOnlyList<string> Args, string Before, string After);

/// <summary>
/// Sends the user to a provider's own sign-in (it opens the browser) by running the tool's login command with the
/// terminal handed over to it. The proxy the user set is passed along, since the sign-in servers are as unreachable
/// as the models without it.
/// </summary>
public static class ToolLogin
{
    public static LoginPlan? PlanFor(ConnectionKind kind, bool deviceCode = false) => kind switch
    {
        ConnectionKind.ClaudeCode => new LoginPlan(kind, "Signing in to Claude Code",
            ["claude", "auth", "login"],
            "Your browser opens at Anthropic. Sign in with the account that has your Claude subscription.",
            "Back in Tokonomy."),
        ConnectionKind.Codex when deviceCode => new LoginPlan(kind, "Signing in to Codex with a device code",
            ["codex", "login", "--device-auth"],
            "Codex shows a code and an address. Open the address on any device, enter the code and sign in with ChatGPT.",
            "Back in Tokonomy."),
        ConnectionKind.Codex => new LoginPlan(kind, "Signing in to Codex",
            ["codex", "login"],
            "Your browser opens at OpenAI. Sign in with your ChatGPT account. (For an API key instead, run: codex login --with-api-key)",
            "Back in Tokonomy."),
        ConnectionKind.Antigravity => new LoginPlan(kind, "Signing in to Antigravity",
            ["agy"],
            "Antigravity has no separate sign-in command, so it starts normally. Sign in with your Google account when it asks, then quit it (Ctrl+C, or /exit) to come back here.",
            "Back in Tokonomy."),
        _ => null,
    };

    /// <summary>Runs the login in the foreground with the console inherited; returns the tool's exit code.</summary>
    public static async Task<int> RunAsync(LoginPlan plan, string workingDirectory, CancellationToken ct, ProxySettings? proxy = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        proxy ??= ProxySettings.Current.ForProvider(plan.Kind switch
        {
            ConnectionKind.ClaudeCode => "claude",
            ConnectionKind.Codex => "codex",
            _ => "antigravity",
        });
        var spec = new ProcessSpec
        {
            Args = plan.Args,
            WorkingDirectory = workingDirectory,
            Environment = EnvironmentBuilder.ForGate([], null, proxy),
            ReplaceEnvironment = true,
            Proxy = proxy,
        };

        var psi = ProcessRunner.BuildStartInfo(spec, proxy);
        // The user talks to the tool directly: no redirection, so its prompts, links and browser hand-off work.
        psi.RedirectStandardInput = false;
        psi.RedirectStandardOutput = false;
        psi.RedirectStandardError = false;
        psi.StandardInputEncoding = null;
        psi.StandardOutputEncoding = null;
        psi.StandardErrorEncoding = null;
        psi.CreateNoWindow = false;

        using var process = System.Diagnostics.Process.Start(psi)
                            ?? throw new InvalidOperationException($"Could not start '{plan.Args[0]}'.");
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            throw;
        }

        return process.ExitCode;
    }
}
