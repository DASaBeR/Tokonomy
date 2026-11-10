using System.Text.Json;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Config;

/// <summary>One tool Tokonomy can use: is it installed, is it signed in, and how to fix it when not.</summary>
public sealed record ToolState(string Name, bool Installed, bool? SignedIn, string Detail, string InstallHint)
{
    /// <summary>Installed, and signed in when signing in matters.</summary>
    public bool Ready => Installed && SignedIn != false;
}

/// <summary>What this machine has, found once at the start of setup so every menu can say what works and what is missing.</summary>
public sealed record MachineReport
{
    public required ToolState Git { get; init; }
    public required ToolState Claude { get; init; }
    public required ToolState Antigravity { get; init; }
    public required ToolState OpenCode { get; init; }
    public required ToolState Python { get; init; }
    public required ToolState Node { get; init; }

    /// <summary>Models the signed-in Antigravity plan offers (from <c>agy models</c>).</summary>
    public IReadOnlyList<string> AntigravityModels { get; init; } = [];

    /// <summary>Null on systems where this does not apply; otherwise whether Windows Sandbox can be used.</summary>
    public ToolState? WindowsSandbox { get; init; }

    public ToolState For(Connection connection, bool strongRole) => connection.Kind switch
    {
        ConnectionKind.ClaudeCode => Claude,
        ConnectionKind.Antigravity => Antigravity,
        _ => strongRole ? new ToolState("API", true, null, "needs only a key", "") : OpenCode,
    };

    /// <summary>Any agent CLI that can write code (the cheap role needs one).</summary>
    public bool HasCodingAgent => Claude.Ready || Antigravity.Ready || OpenCode.Installed;
}

public static class MachineScan
{
    public static async Task<MachineReport> ScanAsync(IProcessRunner runner, string root, CancellationToken ct)
    {
        var claude = ClaudeAsync(runner, root, ct);
        var agy = AntigravityAsync(runner, root, ct);
        await Task.WhenAll(claude, agy);

        return new MachineReport
        {
            Git = Simple("git", root, "winget install Git.Git   (or https://git-scm.com/downloads)"),
            Claude = claude.Result,
            Antigravity = agy.Result.State,
            AntigravityModels = agy.Result.Models,
            OpenCode = Simple("opencode", root, "npm install -g opencode-ai   (needs Node.js)"),
            Python = Simple("python", root, "winget install Python.Python.3.12   (or https://www.python.org/downloads)"),
            Node = Simple("node", root, "winget install OpenJS.NodeJS.LTS   (or https://nodejs.org)"),
            WindowsSandbox = OperatingSystem.IsWindows() ? Sandbox() : null,
        };
    }

    private static ToolState Simple(string name, string root, string hint)
    {
        var found = ExecutableResolver.TryResolve(name, root);
        return new ToolState(name, found is not null, null, found ?? "not found", hint);
    }

    internal const string ClaudeHint = "npm install -g @anthropic-ai/claude-code   then run `claude` once and sign in";
    internal const string AntigravityHint = "install the Antigravity CLI (`agy`) from Google, then run `agy` once and sign in with your Google account";

    private static async Task<ToolState> ClaudeAsync(IProcessRunner runner, string root, CancellationToken ct)
    {
        if (ExecutableResolver.TryResolve("claude", root) is null)
        {
            return new ToolState("claude", false, null, "not installed", ClaudeHint);
        }

        try
        {
            var outcome = await runner.RunAsync(new ProcessSpec { Args = ["claude", "auth", "status"], WorkingDirectory = root, Timeout = TimeSpan.FromSeconds(30) }, ct);
            using var doc = JsonDocument.Parse(outcome.Stdout.Trim());
            var loggedIn = doc.RootElement.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
            var method = doc.RootElement.TryGetProperty("authMethod", out var m) ? m.GetString() : null;
            return loggedIn
                ? new ToolState("claude", true, true, method is null ? "signed in" : $"signed in ({method})", "")
                : new ToolState("claude", true, false, "installed, not signed in", "run `claude auth login`");
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ExecutableNotFoundException)
        {
            // Cannot tell: treat it as usable and let the connection test say more.
            return new ToolState("claude", true, null, "installed", "");
        }
    }

    private static async Task<(ToolState State, IReadOnlyList<string> Models)> AntigravityAsync(IProcessRunner runner, string root, CancellationToken ct)
    {
        if (ExecutableResolver.TryResolve("agy", root) is null)
        {
            return (new ToolState("agy", false, null, "not installed", AntigravityHint), []);
        }

        try
        {
            var outcome = await runner.RunAsync(new ProcessSpec { Args = ["agy", "models"], WorkingDirectory = root, Timeout = TimeSpan.FromSeconds(60) }, ct);
            var models = ParseModels(outcome.Stdout);
            return outcome.Succeeded && models.Count > 0
                ? (new ToolState("agy", true, true, $"signed in, {models.Count} model(s) in your plan", ""), models)
                : (new ToolState("agy", true, false, "installed, but it listed no models (not signed in?)", "run `agy` once and sign in"), []);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ExecutableNotFoundException)
        {
            return (new ToolState("agy", true, null, "installed", ""), []);
        }
    }

    public static List<string> ParseModels(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('\t')[0].Trim())
            .Where(id => id.Length > 0 && !id.Contains(' '))
            .ToList();

    private static ToolState Sandbox()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsSandbox.exe");
        return File.Exists(exe)
            ? new ToolState("Windows Sandbox", true, null, "available", "")
            : new ToolState("Windows Sandbox", false, null, "not available (needs Windows Pro/Enterprise/Education with the feature turned on)",
                "Settings > Apps > Optional features > More Windows features > Windows Sandbox, then restart (virtualization must be enabled)");
    }
}
