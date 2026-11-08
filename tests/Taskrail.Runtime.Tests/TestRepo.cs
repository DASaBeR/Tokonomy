using System.Diagnostics;

namespace Taskrail.Runtime.Tests;

/// <summary>A throwaway git repository with one initial commit.</summary>
internal sealed class TestRepo : IDisposable
{
    private readonly TempDir _dir = new();

    public TestRepo(params (string Path, string Content)[] files)
    {
        Git("init", "-q", "-b", "main");
        foreach (var (path, content) in files.Length == 0 ? [("README.md", "# demo\n")] : files)
        {
            Write(path, content);
        }

        Commit("initial");
    }

    public string Path => _dir.Path;

    public string Head => Git("rev-parse", "HEAD").Trim();

    public void Write(string relative, string content) => _dir.File(relative, content);

    public void Commit(string message)
    {
        Git("add", "-A");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", message);
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "safe.directory=*", .. args])
        {
            psi.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? stdout : throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        _dir.Dispose();
    }
}

internal static class Shell
{
    /// <summary>A cross-platform command array.</summary>
    public static List<string> Cmd(string unix, string windows) =>
        OperatingSystem.IsWindows()
            ? ["powershell", "-NoProfile", "-NonInteractive", "-Command", windows]
            : ["sh", "-c", unix];

    /// <summary>Gate that passes when <paramref name="file"/> contains <paramref name="text"/>.</summary>
    public static List<string> FileContains(string file, string text) => Cmd(
        $"grep -q '{text}' '{file}'",
        $"if ((Test-Path '{file}') -and ((Get-Content -Raw '{file}') -match '{text}')) {{ exit 0 }} else {{ Write-Output 'missing {text} in {file}'; exit 1 }}");
}
