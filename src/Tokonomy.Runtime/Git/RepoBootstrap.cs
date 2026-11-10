using System.Text;

namespace Tokonomy.Runtime.Git;

/// <summary>What <see cref="RepoBootstrap.EnsureAsync"/> had to do to make a folder usable.</summary>
public sealed record BootstrapResult(bool InitializedRepo, bool WroteGitIgnore, bool CreatedInitialCommit, string Head)
{
    public bool ChangedAnything => InitializedRepo || WroteGitIgnore || CreatedInitialCommit;
}

/// <summary>
/// Makes a folder ready for Tokonomy without the user running git by hand: runs <c>git init</c> when there is no
/// repository and creates a first commit when the repository has none, because runs branch from a commit and work in
/// git worktrees. A repository that already has commits is never touched.
/// </summary>
public static class RepoBootstrap
{
    /// <summary>
    /// Written only when the folder has no .gitignore, so the first commit does not sweep up dependencies, build output
    /// or secrets files.
    /// </summary>
    internal const string DefaultGitIgnore = """
        # Added by Tokonomy before the first commit (only because this folder had no .gitignore).
        node_modules/
        bin/
        obj/
        dist/
        build/
        .venv/
        venv/
        __pycache__/
        *.pyc
        .env
        .env.*
        *.log
        .DS_Store
        Thumbs.db
        .idea/
        .vs/
        """;

    public static async Task<BootstrapResult> EnsureAsync(GitCli git, string root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Directory not found: {root}");
        }

        var initialized = false;
        if (string.IsNullOrWhiteSpace(await git.RunAsync(root, ["rev-parse", "--git-dir"], ct, allowFailure: true)))
        {
            await git.RunAsync(root, ["init", "--quiet"], ct);
            initialized = true;
        }

        var head = (await git.RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], ct, allowFailure: true)).Trim();
        if (head.Length > 0)
        {
            return new BootstrapResult(initialized, false, false, head);
        }

        // A repository without any commit: write a safety .gitignore if there is none, then commit what is there.
        var wroteIgnore = false;
        var top = (await git.RunAsync(root, ["rev-parse", "--show-toplevel"], ct)).Trim();
        var ignorePath = Path.Combine(Path.GetFullPath(top), ".gitignore");
        if (!File.Exists(ignorePath))
        {
            await File.WriteAllTextAsync(ignorePath, DefaultGitIgnore.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false), ct);
            wroteIgnore = true;
        }

        await git.RunAsync(root, ["add", "-A"], ct);
        var hasIdentity = !string.IsNullOrWhiteSpace(await git.RunAsync(root, ["config", "user.email"], ct, allowFailure: true));
        List<string> identity = hasIdentity ? [] : ["-c", "user.name=Tokonomy", "-c", "user.email=tokonomy@localhost"];
        await git.RunAsync(root, [.. identity, "commit", "--quiet", "--allow-empty", "--no-verify", "-m", "Initial commit (created by Tokonomy)"], ct);
        head = (await git.RunAsync(root, ["rev-parse", "HEAD"], ct)).Trim();
        return new BootstrapResult(initialized, wroteIgnore, true, head);
    }
}
