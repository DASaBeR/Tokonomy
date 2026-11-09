using System.Diagnostics;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;

namespace Tokonomy.E2E.Tests;

/// <summary>A scripted "agent": each call runs the next action against the worktree.</summary>
internal sealed class FakeExecutor(params Action<Brief, string>[] script) : IExecutor
{
    private int _calls;

    public string Name => "fake";

    public ExecutorCapabilities Capabilities { get; } = new() { Headless = true, ModelOverride = true };

    public List<(Brief Brief, string? Model)> Calls { get; } = [];

    public Task<RunResult> RunAsync(Brief brief, string workdir, string? model, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((brief, model));
        var action = script[Math.Min(_calls++, script.Length - 1)];
        action(brief, workdir);
        return Task.FromResult(new RunResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(5) });
    }

    public static Action<Brief, string> Write(string file, string content) =>
        (_, dir) => File.WriteAllText(Path.Combine(dir, file), content);
}

/// <summary>A scripted architect: replies in order, repeating the last one, and records every request.</summary>
internal sealed class FakeArchitect(params string[] replies) : IProvider
{
    private int _next;

    public List<ProviderRequest> Requests { get; } = [];

    public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var text = replies[Math.Min(_next++, replies.Length - 1)];
        return Task.FromResult(new ProviderResponse(text, request.Model, 2000, 300, 0, 0.02m));
    }

    public static string Edit(string path, string search, string replace, string summary = "Fixed it", string why = "The worker got it wrong") =>
        $"<<<<<<< SEARCH {path}\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\nSUMMARY: {summary}\nWHY: {why}\n";
}

internal sealed class TestRepo : IDisposable
{
    public TestRepo(params (string Path, string Content)[] files)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tokonomy-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Git("init", "-q", "-b", "main");
        foreach (var (p, c) in files)
        {
            Write(p, c);
        }

        Git("add", "-A");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "initial");
    }

    public string Path { get; }

    public string Head => Git("rev-parse", "HEAD").Trim();

    public void Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "safe.directory=*", .. args])
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? stdout : throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class Shell
{
    public static List<string> Cmd(string unix, string windows) =>
        OperatingSystem.IsWindows()
            ? ["powershell", "-NoProfile", "-NonInteractive", "-Command", windows]
            : ["sh", "-c", unix];

    /// <summary>Passes when <paramref name="file"/> contains <paramref name="text"/>.</summary>
    public static List<string> FileContains(string file, string text) => Cmd(
        $"grep -q '{text}' '{file}' || {{ echo 'missing {text} in {file}'; exit 1; }}",
        $"if ((Test-Path '{file}') -and ((Get-Content -Raw '{file}') -match '{text}')) {{ exit 0 }} else {{ Write-Output 'missing {text} in {file}'; exit 1 }}");

    public static string YamlArray(IEnumerable<string> items) =>
        "[" + string.Join(", ", items.Select(i => System.Text.Json.JsonSerializer.Serialize(i))) + "]";
}
