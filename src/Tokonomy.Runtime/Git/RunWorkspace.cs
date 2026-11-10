using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tokonomy.Runtime.Git;

public sealed record UnitDiff(IReadOnlyList<string> ChangedPaths, int AddedLines, int DeletedLines, string Patch, bool AddedDependency)
{
    public int TotalLines => AddedLines + DeletedLines;
}

/// <summary>A configured worktree location that is not safe to use.</summary>
public sealed class UnsafeWorktreeRootException(string message) : InvalidOperationException(message);

/// <summary>
/// One worktree per run on branch <c>tokonomy/&lt;run-id&gt;</c>, one commit per accepted unit. Lives under
/// <see cref="WorktreeRoot"/> (outside temp and outside the repository).
/// <para>
/// Every git command for the worktree passes <c>--git-dir</c> and <c>--work-tree</c> explicitly, using the git dir
/// recorded when the worktree was created. The worktree's own <c>.git</c> file is just a pointer the worker can rewrite;
/// trusting it would let a worker aim Tokonomy's <c>reset --hard</c> or <c>commit</c> at another repository.
/// </para>
/// </summary>
public sealed class RunWorkspace
{
    /// <summary>Paths Tokonomy itself writes inside the worktree; never part of a unit's diff.</summary>
    public static readonly string[] ExcludedPathspecs = [":(exclude).tokonomy/tmp", ":(exclude).tokonomy/runs"];

    private readonly GitCli _git;
    private readonly string _gitDir;
    private readonly string _gitLink;

    private RunWorkspace(GitCli git, string repoRoot, string path, string branch, string baseSha, string gitDir, string gitLink)
    {
        _git = git;
        RepoRoot = repoRoot;
        Path = path;
        Branch = branch;
        BaseSha = baseSha;
        _gitDir = gitDir;
        _gitLink = gitLink;
    }

    public string RepoRoot { get; }
    public string Path { get; }
    public string Branch { get; }
    public string BaseSha { get; }

    /// <summary>The worktree path for a run. Throws <see cref="UnsafeWorktreeRootException"/> when the root is unsafe.</summary>
    public static string DefaultLocation(string repoRoot, string runId, string? root = null)
    {
        var effective = WorktreeRoot.Effective(root, repoRoot);
        var problems = WorktreeRoot.Validate(effective, repoRoot);
        if (problems.Count > 0)
        {
            throw new UnsafeWorktreeRootException(
                $"Refusing to create worktrees in {effective ?? "(unknown)"}: {string.Join("; ", problems)}. Set isolation.worktree_root to a safe directory.");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(repoRoot))))[..8];
        return System.IO.Path.Combine(effective!, hash, runId);
    }

    public static async Task<RunWorkspace> CreateAsync(GitCli git, string repoRoot, string runId, string baseSha, CancellationToken ct, string? location = null, string? root = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        var path = System.IO.Path.GetFullPath(location ?? DefaultLocation(repoRoot, runId, root));
        if (WorktreeRoot.Overlaps(path, System.IO.Path.GetFullPath(repoRoot)))
        {
            throw new UnsafeWorktreeRootException($"Refusing to create a worktree at {path}: it overlaps the repository.");
        }

        var branch = $"tokonomy/{runId}";
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new GitException($"Worktree path already exists: {path}");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (WorktreeGuard.IsLink(System.IO.Path.GetDirectoryName(path)!))
        {
            throw new UnsafeWorktreeRootException($"Refusing to create a worktree under {System.IO.Path.GetDirectoryName(path)}: it is a link.");
        }

        await git.RunAsync(repoRoot, ["worktree", "add", "--quiet", "-b", branch, path, baseSha], ct);

        // Recorded once, from a freshly created worktree nobody else has touched yet.
        var gitDir = System.IO.Path.GetFullPath((await git.RunAsync(path, ["rev-parse", "--absolute-git-dir"], ct)).Trim());
        var gitLink = await File.ReadAllTextAsync(System.IO.Path.Combine(path, ".git"), ct);
        return new RunWorkspace(git, repoRoot, path, branch, baseSha, gitDir, gitLink);
    }

    /// <summary>Runs git against this worktree only, never trusting the worktree's <c>.git</c> pointer.</summary>
    private Task<string> Git(IEnumerable<string> args, CancellationToken ct, bool allowFailure = false) =>
        _git.RunAsync(Path, ["--git-dir=" + _gitDir, "--work-tree=" + Path, .. args], ct, allowFailure);

    public async Task<string> HeadAsync(CancellationToken ct) => (await Git(["rev-parse", "HEAD"], ct)).Trim();

    /// <summary>
    /// Checks that the worktree's <c>.git</c> pointer is still the file git wrote. If it was replaced (by a link, a
    /// directory or other content) it is put back and a description of the tampering is returned; null when intact.
    /// </summary>
    public string? VerifyGitLink()
    {
        var full = System.IO.Path.Combine(Path, ".git");
        string? problem = null;
        try
        {
            if (WorktreeGuard.IsLink(full))
            {
                problem = ".git was replaced by a link";
                WorktreeGuard.DeleteFile(Path, ".git");
            }
            else if (Directory.Exists(full))
            {
                problem = ".git was replaced by a directory";
                Directory.Move(full, full + ".tampered-" + Guid.NewGuid().ToString("N")[..8]);
            }
            else if (WorktreeGuard.ReadAllText(Path, ".git") is var current && current != _gitLink)
            {
                problem = current is null ? ".git was deleted" : ".git was rewritten";
            }

            if (problem is not null)
            {
                WorktreeGuard.WriteAllText(Path, ".git", _gitLink);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem ??= $".git could not be verified: {ex.Message}";
        }

        return problem;
    }

    /// <summary>Stages everything (except Tokonomy's own paths) and diffs against <paramref name="since"/>.</summary>
    public async Task<UnitDiff> DiffAsync(string since, CancellationToken ct)
    {
        await Git(["add", "-A", "--", ".", .. ExcludedPathspecs], ct);
        var numstat = await Git(["diff", "--cached", "--numstat", "--no-renames", since], ct);
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

        var patch = paths.Count == 0 ? "" : await Git(["diff", "--cached", "--no-renames", since], ct);
        var addedDependency = await DependencyDetector.DetectAsync(paths, patch, p => ShowAsync(since, p, ct), p => ReadWorktreeFile(p));
        return new UnitDiff(paths, added, deleted, patch, addedDependency);
    }

    public async Task<string> CommitAsync(string message, CancellationToken ct)
    {
        await Git(["add", "-A", "--", ".", .. ExcludedPathspecs], ct);
        // Gates already ran; repo hooks are skipped so they cannot alter accepted content. A fallback identity is
        // used only when the user has none configured.
        var hasIdentity = !string.IsNullOrWhiteSpace(await Git(["config", "user.email"], ct, allowFailure: true));
        List<string> args = hasIdentity ? [] : ["-c", "user.name=Tokonomy", "-c", "user.email=tokonomy@localhost"];
        await Git([.. args, "commit", "--quiet", "--no-verify", "--allow-empty", "-m", message], ct);
        return await HeadAsync(ct);
    }

    /// <summary>
    /// Discards everything since <paramref name="sha"/>. Ignored files (deps, build output) are kept. Links that lead
    /// outside the worktree are removed first (except <paramref name="keepLinks"/>, e.g. links a prepare step created),
    /// so git never writes tracked files through one.
    /// </summary>
    public async Task ResetAsync(string sha, CancellationToken ct, IReadOnlyCollection<string>? keepLinks = null)
    {
        var keep = new HashSet<string>(keepLinks ?? [], StringComparer.OrdinalIgnoreCase);
        WorktreeGuard.Unlink(WorktreeGuard.FindLinks(Path).Where(l => l.Escapes && !keep.Contains(l.RelativePath)));
        VerifyGitLink();
        await Git(["reset", "--quiet", "--hard", sha], ct);
        await Git(["clean", "-fdq", "-e", ".tokonomy/tmp"], ct);
    }

    /// <summary>
    /// Removes the worktree. Returns null on success, or why the directory was left in place.
    /// <para>
    /// <c>git worktree remove --force</c> follows junctions on Windows and deletes what they point to (verified with
    /// git for Windows), so every link inside the worktree is removed as a link first, and if any link cannot be
    /// removed the worktree is left on disk rather than risk deleting files outside it.
    /// </para>
    /// </summary>
    public async Task<string?> RemoveAsync(CancellationToken ct)
    {
        var stuck = WorktreeGuard.Unlink(WorktreeGuard.FindLinks(Path));
        var remaining = stuck.Count > 0 ? stuck : WorktreeGuard.FindLinks(Path);
        if (remaining.Count > 0)
        {
            return $"left the worktree at {Path} in place: it still contains links ({string.Join(", ", remaining.Take(5).Select(l => l.RelativePath))}) and removing it could follow them";
        }

        VerifyGitLink();

        // Windows can hold file locks briefly after processes exit (AV scanners, build servers): retry.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _git.RunAsync(RepoRoot, ["worktree", "remove", "--force", Path], ct);
                RemoveEmptyParent();
                return null;
            }
            catch (GitException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
                WorktreeGuard.Unlink(WorktreeGuard.FindLinks(Path));
            }
            catch (GitException ex)
            {
                await _git.RunAsync(RepoRoot, ["worktree", "prune"], ct, allowFailure: true);
                return $"could not remove the worktree at {Path}: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Untracked files git ignores, with fully ignored directories collapsed to one entry ending in <c>/</c>. Changes
    /// there never appear in a diff, so they are where a worker could hide something that a gate then runs.
    /// </summary>
    public async Task<IReadOnlySet<string>> IgnoredEntriesAsync(CancellationToken ct)
    {
        var output = await Git(["ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z"], ct);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith(".tokonomy/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The files a gate environment receives: tracked plus new non-ignored files, as git lists them through the
    /// recorded git dir (never the worktree's own <c>.git</c> pointer). Tokonomy's paths are left out.
    /// </summary>
    public async Task<IReadOnlyList<string>> SourceFilesAsync(CancellationToken ct) =>
        ParseFileList(await Git(["ls-files", "-z", "--cached", "--others", "--exclude-standard"], ct));

    /// <summary>The same list for a plain clone (bench acceptance), where the repository's own <c>.git</c> is Tokonomy's.</summary>
    public static async Task<IReadOnlyList<string>> SourceFilesAsync(GitCli git, string repo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        return ParseFileList(await git.RunAsync(repo, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], ct));
    }

    private static List<string> ParseFileList(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith(".tokonomy/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Removes a worktree left behind by an interrupted run, the same safe way as <see cref="RemoveAsync"/>: links are
    /// removed as links first, and the directory is left alone if any remain. Returns null on success, else why not.
    /// </summary>
    public static async Task<string?> RemoveLeftoverAsync(GitCli git, string repoRoot, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        if (Directory.Exists(path))
        {
            if (WorktreeGuard.IsLink(path))
            {
                return $"{path} is itself a link; not touching it";
            }

            var stuck = WorktreeGuard.Unlink(WorktreeGuard.FindLinks(path));
            if (stuck.Count > 0 || WorktreeGuard.FindLinks(path).Count > 0)
            {
                return $"{path} still contains links that could not be removed; remove them by hand first";
            }
        }

        await git.RunAsync(repoRoot, ["worktree", "remove", "--force", path], ct, allowFailure: true);
        await git.RunAsync(repoRoot, ["worktree", "prune"], ct, allowFailure: true);
        return Directory.Exists(path) ? $"git could not remove {path}" : null;
    }

    /// <summary>The per-repository folder (<c>&lt;root&gt;/&lt;hash&gt;</c>) is removed once its last worktree is gone.</summary>
    private void RemoveEmptyParent()
    {
        try
        {
            var parent = System.IO.Path.GetDirectoryName(Path);
            if (parent is not null && Directory.Exists(parent) && !WorktreeGuard.IsLink(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent, recursive: false);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<string?> ShowAsync(string rev, string path, CancellationToken ct)
    {
        var text = await Git(["show", $"{rev}:{path}"], ct, allowFailure: true);
        return text.Length == 0 ? null : text;
    }

    private string? ReadWorktreeFile(string relative)
    {
        try
        {
            return WorktreeGuard.ReadAllText(Path, relative);
        }
        catch (IOException)
        {
            return null;   // a link or an unreadable file: not something a dependency check should follow
        }
    }
}
