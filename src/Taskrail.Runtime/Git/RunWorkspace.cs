using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Taskrail.Runtime.Git;

public sealed record UnitDiff(IReadOnlyList<string> ChangedPaths, int AddedLines, int DeletedLines, string Patch, bool AddedDependency)
{
    public int TotalLines => AddedLines + DeletedLines;
}

/// <summary>
/// One worktree per run on branch <c>taskrail/&lt;run-id&gt;</c>, one commit per accepted unit.
/// Lives under the system temp directory so build tools that search upward never see the main repo.
/// </summary>
public sealed class RunWorkspace
{
    /// <summary>Paths Taskrail itself writes inside the worktree; never part of a unit's diff.</summary>
    public static readonly string[] ExcludedPathspecs = [":(exclude).taskrail/tmp", ":(exclude).taskrail/runs"];

    private readonly GitCli _git;

    private RunWorkspace(GitCli git, string repoRoot, string path, string branch, string baseSha)
    {
        _git = git;
        RepoRoot = repoRoot;
        Path = path;
        Branch = branch;
        BaseSha = baseSha;
    }

    public string RepoRoot { get; }
    public string Path { get; }
    public string Branch { get; }
    public string BaseSha { get; }

    public static string DefaultLocation(string repoRoot, string runId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(repoRoot))))[..8];
        return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "taskrail", hash, runId);
    }

    public static async Task<RunWorkspace> CreateAsync(GitCli git, string repoRoot, string runId, string baseSha, CancellationToken ct, string? location = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        var path = location ?? DefaultLocation(repoRoot, runId);
        var branch = $"taskrail/{runId}";
        if (Directory.Exists(path))
        {
            throw new GitException($"Worktree path already exists: {path}");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await git.RunAsync(repoRoot, ["worktree", "add", "--quiet", "-b", branch, path, baseSha], ct);
        return new RunWorkspace(git, repoRoot, path, branch, baseSha);
    }

    public Task<string> HeadAsync(CancellationToken ct) => _git.HeadAsync(Path, ct);

    /// <summary>Stages everything (except Taskrail's own paths) and diffs against <paramref name="since"/>.</summary>
    public async Task<UnitDiff> DiffAsync(string since, CancellationToken ct)
    {
        await _git.RunAsync(Path, ["add", "-A", "--", ".", .. ExcludedPathspecs], ct);
        var numstat = await _git.RunAsync(Path, ["diff", "--cached", "--numstat", "--no-renames", since], ct);
        var paths = new List<string>();
        int added = 0, deleted = 0;
        foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 3);
            if (parts.Length != 3)
            {
                continue;
            }

            // Binary files report "-"; count them as one line each so they still register as changes.
            added += parts[0] == "-" ? 1 : int.Parse(parts[0], CultureInfo.InvariantCulture);
            deleted += parts[1] == "-" ? 0 : int.Parse(parts[1], CultureInfo.InvariantCulture);
            paths.Add(parts[2].Trim());
        }

        var patch = paths.Count == 0 ? "" : await _git.RunAsync(Path, ["diff", "--cached", "--no-renames", since], ct);
        var addedDependency = await DependencyDetector.DetectAsync(paths, patch, p => _git.ShowFileAsync(Path, since, p, ct), p => ReadWorktreeFile(p));
        return new UnitDiff(paths, added, deleted, patch, addedDependency);
    }

    public async Task<string> CommitAsync(string message, CancellationToken ct)
    {
        await _git.RunAsync(Path, ["add", "-A", "--", ".", .. ExcludedPathspecs], ct);
        // Gates already ran; repo hooks are skipped so they cannot alter accepted content. A fallback identity is
        // used only when the user has none configured.
        var hasIdentity = !string.IsNullOrWhiteSpace(await _git.RunAsync(Path, ["config", "user.email"], ct, allowFailure: true));
        List<string> args = hasIdentity ? [] : ["-c", "user.name=Taskrail", "-c", "user.email=taskrail@localhost"];
        await _git.RunAsync(Path, [.. args, "commit", "--quiet", "--no-verify", "--allow-empty", "-m", message], ct);
        return await HeadAsync(ct);
    }

    /// <summary>Discards everything since <paramref name="sha"/>. Ignored files (deps, build output) are kept.</summary>
    public async Task ResetAsync(string sha, CancellationToken ct)
    {
        await _git.RunAsync(Path, ["reset", "--quiet", "--hard", sha], ct);
        await _git.RunAsync(Path, ["clean", "-fdq", "-e", ".taskrail/tmp"], ct);
    }

    public async Task RemoveAsync(CancellationToken ct)
    {
        // Windows can hold file locks briefly after processes exit (AV scanners, build servers): retry.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _git.RunAsync(RepoRoot, ["worktree", "remove", "--force", Path], ct);
                return;
            }
            catch (GitException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
            catch (GitException)
            {
                await _git.RunAsync(RepoRoot, ["worktree", "prune"], ct, allowFailure: true);
                return;
            }
        }
    }

    private string? ReadWorktreeFile(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }
}
