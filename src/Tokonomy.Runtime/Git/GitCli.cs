using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Git;

public sealed class GitException(string message) : Exception(message);

/// <summary>Thin wrapper over the git CLI (more reliable than libgit2 for worktrees).</summary>
public sealed class GitCli(IProcessRunner runner)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<string> RunAsync(string cwd, IEnumerable<string> args, CancellationToken ct, bool allowFailure = false)
    {
        // safe.directory=* avoids "dubious ownership" failures on file systems that don't record owners
        // (FAT/exFAT, some network shares). Tokonomy only operates on repos the user pointed it at.
        List<string> argv = ["git", "-c", "safe.directory=*", "-c", "core.quotepath=off", .. args];
        var outcome = await runner.RunAsync(new ProcessSpec
        {
            Args = argv,
            WorkingDirectory = cwd,
            Timeout = DefaultTimeout,
            Environment = Environment(),
            ReplaceEnvironment = true,
        }, ct);
        if (!outcome.Succeeded && !allowFailure)
        {
            throw new GitException($"git {string.Join(' ', argv.Skip(5))} failed ({outcome.ExitCode}): {outcome.Stderr.Trim()}");
        }

        return outcome.Stdout;
    }

    /// <summary>
    /// The parent environment without credentials and without any <c>GIT_*</c> variable: <c>GIT_DIR</c>,
    /// <c>GIT_WORK_TREE</c>, <c>GIT_INDEX_FILE</c>, <c>GIT_CONFIG_*</c> and friends left over in the user's shell would
    /// silently point Tokonomy's commits and resets at a different repository or index. Prompts are disabled.
    /// </summary>
    internal static Dictionary<string, string> Environment(System.Collections.IDictionary? parent = null)
    {
        var env = EnvironmentBuilder.ForGate([], parent);
        foreach (var key in env.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            env.Remove(key);
        }

        env["GIT_TERMINAL_PROMPT"] = "0";
        return env;
    }

    public async Task<string> HeadAsync(string cwd, CancellationToken ct) =>
        (await RunAsync(cwd, ["rev-parse", "HEAD"], ct)).Trim();

    public async Task<string> ResolveAsync(string cwd, string rev, CancellationToken ct) =>
        (await RunAsync(cwd, ["rev-parse", "--verify", rev + "^{commit}"], ct)).Trim();

    public async Task<string> TopLevelAsync(string cwd, CancellationToken ct) =>
        Path.GetFullPath((await RunAsync(cwd, ["rev-parse", "--show-toplevel"], ct)).Trim());

    public async Task<bool> IsCleanAsync(string cwd, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(await RunAsync(cwd, ["status", "--porcelain", "--untracked-files=no"], ct));

    public async Task<string?> ShowFileAsync(string cwd, string rev, string path, CancellationToken ct)
    {
        var text = await RunAsync(cwd, ["show", $"{rev}:{path}"], ct, allowFailure: true);
        return text.Length == 0 ? null : text;
    }
}
