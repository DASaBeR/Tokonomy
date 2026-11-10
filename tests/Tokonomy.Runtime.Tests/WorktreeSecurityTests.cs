using System.Diagnostics;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Tests;

/// <summary>
/// Creates the kinds of links a worker (or code a gate runs) could plant. On Windows: junctions and hard links, which
/// need no administrator rights or developer mode, exactly what an unprivileged process can make. Elsewhere: symlinks.
/// </summary>
internal static class FsLinks
{
    public static void Directory(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Run("cmd", "/c", "mklink", "/J", link, target);
        }
        else
        {
            System.IO.Directory.CreateSymbolicLink(link, target);
        }
    }

    public static void HardLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Run("cmd", "/c", "mklink", "/H", link, target);
        }
        else
        {
            Run("ln", target, link);
        }
    }

    private static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {string.Join(' ', args)}: {err}");
        }
    }
}

/// <summary>A worktree-like directory plus an "outside" directory holding a secret and a file that must survive.</summary>
internal sealed class EscapeLab : IDisposable
{
    private readonly TempDir _dir = new();

    public EscapeLab()
    {
        System.IO.Directory.CreateDirectory(Root);
        System.IO.Directory.CreateDirectory(Outside);
        File.WriteAllText(Secret, "OUTSIDE-SECRET");
        File.WriteAllText(Path.Combine(Root, "a.txt"), "inside\n");
    }

    public string Root => Path.Combine(_dir.Path, "wt");
    public string Outside => Path.Combine(_dir.Path, "outside");
    public string Secret => Path.Combine(Outside, "secret.txt");

    public void Dispose()
    {
        foreach (var link in WorktreeGuard.FindLinks(_dir.Path))
        {
            WorktreeGuard.Unlink([link]);
        }

        _dir.Dispose();
    }
}

public class WorktreeGuardPathTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "guard-root"));

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("a/../b")]            // even when it would stay inside: '..' is never needed
    [InlineData("./a")]
    [InlineData("a//b")]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("a:b")]               // alternate data stream on Windows
    [InlineData("file.txt::$DATA")]
    public void Rejects_traversal_rooted_and_stream_paths(string relative) =>
        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.ResolveInside(Root, relative));

    [Fact]
    public void Rejects_absolute_and_unc_paths()
    {
        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.ResolveInside(Root, Path.Combine(Path.GetTempPath(), "x")));
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.ResolveInside(Root, @"\\server\share\x"));
            Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.ResolveInside(Root, @"\\?\C:\x"));
            Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.ResolveInside(Root, @"C:x"));
        }
    }

    [Theory]
    [InlineData(".tokonomy./policy.yaml")]   // Win32 strips the dot: this IS .tokonomy
    [InlineData(".git /config")]             // and the space
    [InlineData("src/CON")]
    [InlineData("nul.txt")]
    [InlineData("com1.py")]
    [InlineData("LPT9")]
    public void Rejects_names_windows_silently_rewrites(string relative)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // these are ordinary names on Linux and macOS
        }

        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.ResolveInside(Root, relative));
    }

    [Theory]
    [InlineData("src/a.py")]
    [InlineData("src\\a.py")]
    [InlineData("docs/con-figure.md")]
    [InlineData(".gitignore")]
    public void Accepts_ordinary_paths(string relative) =>
        Assert.StartsWith(Root + Path.DirectorySeparatorChar, WorktreeGuard.ResolveInside(Root, relative), StringComparison.Ordinal);

    [Fact]
    public void Containment_is_case_insensitive_on_windows_and_respects_directory_boundaries()
    {
        Assert.Equal(OperatingSystem.IsWindows(), WorktreeGuard.IsInside(Root, Root.ToUpperInvariant() + Path.DirectorySeparatorChar + "x"));
        Assert.False(WorktreeGuard.IsInside(Root, Root + "-sibling" + Path.DirectorySeparatorChar + "x"));
        Assert.False(WorktreeGuard.IsInside(Root, Root));
    }
}

public class WorktreeGuardLinkTests
{
    [Fact]
    public void Reading_or_writing_through_a_directory_link_is_refused_and_nothing_outside_changes()
    {
        using var lab = new EscapeLab();
        FsLinks.Directory(Path.Combine(lab.Root, "j"), lab.Outside);

        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.ReadAllText(lab.Root, "j/secret.txt"));
        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.WriteAllText(lab.Root, "j/pwn.txt", "x"));
        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.WriteAllText(lab.Root, "j/secret.txt", "overwritten"));
        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.WriteAllText(lab.Root, "j/deeper/new.txt", "x"));

        Assert.False(File.Exists(Path.Combine(lab.Outside, "pwn.txt")));
        Assert.False(Directory.Exists(Path.Combine(lab.Outside, "deeper")));
        Assert.Equal("OUTSIDE-SECRET", File.ReadAllText(lab.Secret));
    }

    [Fact]
    public void A_different_case_spelling_of_the_link_is_refused_too()
    {
        using var lab = new EscapeLab();
        FsLinks.Directory(Path.Combine(lab.Root, "Junc"), lab.Outside);
        if (!OperatingSystem.IsWindows())
        {
            return;   // case-sensitive file systems: "JUNC" is simply a different, missing name
        }

        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.ReadAllText(lab.Root, "JUNC/secret.txt"));
        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.WriteAllText(lab.Root, "jUnC/x.txt", "x"));
    }

    [Fact]
    public void A_hard_link_to_an_outside_file_is_refused()
    {
        using var lab = new EscapeLab();
        FsLinks.HardLink(Path.Combine(lab.Root, "h.txt"), lab.Secret);
        if (!OperatingSystem.IsWindows())
        {
            return;   // link counts are checked through the Windows handle; documented limitation elsewhere
        }

        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.WriteAllText(lab.Root, "h.txt", "overwritten"));
        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.ReadAllText(lab.Root, "h.txt"));
        Assert.Equal("OUTSIDE-SECRET", File.ReadAllText(lab.Secret));
    }

    [Fact]
    public void The_worktree_root_itself_being_a_link_is_refused()
    {
        using var lab = new EscapeLab();
        var linkedRoot = Path.Combine(Path.GetDirectoryName(lab.Root)!, "linked-root");
        FsLinks.Directory(linkedRoot, lab.Outside);

        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.ReadAllText(linkedRoot, "secret.txt"));
        Assert.Throws<WorktreeEscapeException>(() => WorktreeGuard.WriteAllText(linkedRoot, "x.txt", "x"));
        Assert.False(File.Exists(Path.Combine(lab.Outside, "x.txt")));
    }

    [Fact]
    public void Deleting_a_link_removes_the_link_and_never_the_target()
    {
        using var lab = new EscapeLab();
        FsLinks.Directory(Path.Combine(lab.Root, "j"), lab.Outside);

        WorktreeGuard.DeleteFile(lab.Root, "j");

        Assert.False(Directory.Exists(Path.Combine(lab.Root, "j")));
        Assert.Equal("OUTSIDE-SECRET", File.ReadAllText(lab.Secret));
    }

    [Fact]
    public void Ordinary_files_still_round_trip()
    {
        using var lab = new EscapeLab();
        WorktreeGuard.WriteAllText(lab.Root, "src/deep/new.py", "print(1)\n");
        WorktreeGuard.WriteAllText(lab.Root, "a.txt", "short");   // truncates

        Assert.Equal("print(1)\n", WorktreeGuard.ReadAllText(lab.Root, "src/deep/new.py"));
        Assert.Equal("short", WorktreeGuard.ReadAllText(lab.Root, "a.txt"));
        Assert.Null(WorktreeGuard.ReadAllText(lab.Root, "missing.txt"));
    }

    [Fact]
    public void Finding_links_reports_escapes_without_descending_into_them()
    {
        using var lab = new EscapeLab();
        Directory.CreateDirectory(Path.Combine(lab.Root, "inner"));
        Directory.CreateDirectory(Path.Combine(lab.Root, "sub"));
        File.WriteAllText(Path.Combine(lab.Outside, "only-outside.txt"), "x");
        FsLinks.Directory(Path.Combine(lab.Root, "out"), lab.Outside);
        FsLinks.Directory(Path.Combine(lab.Root, "sub", "in"), Path.Combine(lab.Root, "inner"));

        var links = WorktreeGuard.FindLinks(lab.Root);

        var outLink = Assert.Single(links, l => l.RelativePath == "out");
        Assert.True(outLink.Escapes);
        var inLink = Assert.Single(links, l => l.RelativePath == "sub/in");
        Assert.False(inLink.Escapes);
        Assert.Equal(2, links.Count);   // nothing from inside the targets was listed
    }

    [Fact]
    public void Unlink_removes_links_and_leaves_their_targets_intact()
    {
        using var lab = new EscapeLab();
        FsLinks.Directory(Path.Combine(lab.Root, "j"), lab.Outside);

        Assert.Empty(WorktreeGuard.Unlink(WorktreeGuard.FindLinks(lab.Root)));

        Assert.Empty(WorktreeGuard.FindLinks(lab.Root));
        Assert.Equal("OUTSIDE-SECRET", File.ReadAllText(lab.Secret));
    }
}

public class RunWorkspaceSecurityTests
{
    // TempDir lives under temp, which the default validation rightly refuses; these tests exercise the workspace
    // mechanics with an explicit location instead.
    private static async Task<(TestRepo Repo, RunWorkspace Workspace)> NewWorkspaceAt(string location)
    {
        // "store" is ignored, like node_modules: links a dependency install leaves there survive `git clean`.
        var repo = new TestRepo(("app.txt", "v1\n"), (".gitignore", "store\n"));
        var ws = await RunWorkspace.CreateAsync(new GitCli(new ProcessRunner()), repo.Path, "run-sec", repo.Head, default, location: location);
        return (repo, ws);
    }

    [Fact]
    public async Task Removing_a_worktree_never_deletes_what_a_junction_inside_points_to()
    {
        using var scratch = new TempDir();
        var victim = Path.Combine(scratch.Path, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "precious");
        var (repo, ws) = await NewWorkspaceAt(Path.Combine(scratch.Path, "wt"));
        using var _ = repo;
        FsLinks.Directory(Path.Combine(ws.Path, "j"), victim);

        var warning = await ws.RemoveAsync(default);

        Assert.Null(warning);
        Assert.False(Directory.Exists(ws.Path));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(victim, "keep.txt")));   // plain `git worktree remove --force` deletes this
    }

    [Fact]
    public async Task Reset_removes_escaping_links_but_keeps_baseline_ones_and_never_touches_targets()
    {
        using var scratch = new TempDir();
        var victim = Path.Combine(scratch.Path, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "precious");
        var (repo, ws) = await NewWorkspaceAt(Path.Combine(scratch.Path, "wt"));
        using var _ = repo;
        FsLinks.Directory(Path.Combine(ws.Path, "planted"), victim);
        FsLinks.Directory(Path.Combine(ws.Path, "store"), victim);

        await ws.ResetAsync(ws.BaseSha, default, keepLinks: ["store"]);

        Assert.False(Directory.Exists(Path.Combine(ws.Path, "planted")));
        Assert.True(Directory.Exists(Path.Combine(ws.Path, "store")));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(victim, "keep.txt")));
        await ws.RemoveAsync(default);
    }

    [Fact]
    public async Task A_rewritten_git_pointer_is_ignored_detected_and_restored()
    {
        using var scratch = new TempDir();
        var (repo, ws) = await NewWorkspaceAt(Path.Combine(scratch.Path, "wt"));
        using var _ = repo;
        using var other = new TestRepo(("other.txt", "other\n"));
        var otherHead = other.Head;

        // The worker points the worktree at a different repository, then Tokonomy commits and resets. (git marks .git
        // hidden on Windows; an attacker simply clears that.)
        File.SetAttributes(Path.Combine(ws.Path, ".git"), FileAttributes.Normal);
        File.WriteAllText(Path.Combine(ws.Path, ".git"), $"gitdir: {Path.Combine(other.Path, ".git")}\n");
        File.WriteAllText(Path.Combine(ws.Path, "app.txt"), "v2\n");
        await ws.CommitAsync("unit", default);

        Assert.Equal(otherHead, other.Head);                                 // the other repository was not touched
        Assert.Equal("v2\n", repo.Git("show", $"{ws.Branch}:app.txt"));     // the commit went to the run branch

        Assert.Contains("rewritten", ws.VerifyGitLink(), StringComparison.Ordinal);
        Assert.Null(ws.VerifyGitLink());                                      // and it was put back
        await ws.RemoveAsync(default);
    }

    [Fact]
    public async Task Git_variables_in_the_parent_environment_cannot_redirect_tokonomy()
    {
        using var other = new TestRepo(("other.txt", "other\n"));
        var env = GitCli.Environment(new System.Collections.Hashtable
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "",
            ["GIT_DIR"] = Path.Combine(other.Path, ".git"),
            ["GIT_WORK_TREE"] = other.Path,
            ["GIT_INDEX_FILE"] = "x",
            ["GIT_CONFIG_COUNT"] = "1",
        });

        Assert.DoesNotContain(env.Keys, k => k.StartsWith("GIT_", StringComparison.Ordinal) && k != "GIT_TERMINAL_PROMPT");
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_worktree_location_inside_the_repository_is_refused()
    {
        using var repo = new TestRepo(("app.txt", "v1\n"));
        await Assert.ThrowsAsync<UnsafeWorktreeRootException>(() =>
            RunWorkspace.CreateAsync(new GitCli(new ProcessRunner()), repo.Path, "r", repo.Head, default, location: Path.Combine(repo.Path, "wt")));
    }

    [Fact]
    public async Task The_default_root_is_outside_temp_and_is_used_when_nothing_is_configured()
    {
        using var repo = new TestRepo(("app.txt", "v1\n"));
        var ws = await RunWorkspace.CreateAsync(new GitCli(new ProcessRunner()), repo.Path, "r-default", repo.Head, default);
        try
        {
            Assert.StartsWith(WorktreeRoot.Default()!, ws.Path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(WorktreeRoot.TempDirectories(), t => WorktreeRoot.Overlaps(ws.Path, t));
        }
        finally
        {
            await ws.RemoveAsync(default);
        }
    }
}

public class WorktreeRootTests
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void The_default_root_is_valid() =>
        Assert.Empty(WorktreeRoot.Validate(WorktreeRoot.Default(), Path.Combine(Path.GetTempPath(), "some-repo")));

    [Fact]
    public void Temp_and_anything_inside_it_are_refused()
    {
        Assert.NotEmpty(WorktreeRoot.Validate(Path.GetTempPath(), null));
        Assert.NotEmpty(WorktreeRoot.Validate(Path.Combine(Path.GetTempPath(), "tokonomy", "wt"), null));
    }

    [Fact]
    public void A_root_that_contains_temp_is_refused()
    {
        var parentOfTemp = Path.GetDirectoryName(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))!;
        Assert.NotEmpty(WorktreeRoot.Validate(parentOfTemp, null));
    }

    [Fact]
    public void The_repository_and_its_ancestors_are_refused()
    {
        var repo = Path.Combine(Home, "src", "proj");
        Assert.NotEmpty(WorktreeRoot.Validate(Path.Combine(repo, "wt"), repo));
        Assert.NotEmpty(WorktreeRoot.Validate(Path.Combine(Home, "src"), repo));
    }

    [Fact]
    public void Home_a_drive_root_and_credential_folders_are_refused()
    {
        Assert.NotEmpty(WorktreeRoot.Validate(Home, null));
        Assert.NotEmpty(WorktreeRoot.Validate(Path.GetPathRoot(Home), null));
        Assert.NotEmpty(WorktreeRoot.Validate(Path.Combine(Home, ".ssh", "wt"), null));
        Assert.NotEmpty(WorktreeRoot.Validate(Path.Combine(Home, ".gemini", "wt"), null));
        Assert.NotEmpty(WorktreeRoot.Validate(Path.Combine(Home, ".claude"), null));
    }

    [Fact]
    public void An_ordinary_folder_under_home_is_fine() =>
        Assert.Empty(WorktreeRoot.Validate(Path.Combine(Home, "tokonomy-worktrees"), Path.Combine(Home, "src", "proj")));

    [Fact]
    public void A_relative_root_resolves_against_the_repository_and_is_then_checked()
    {
        var repo = Path.Combine(Home, "src", "proj");
        Assert.Equal(Path.Combine(Home, "src", "wts"), WorktreeRoot.Effective("../wts", repo));
        Assert.NotEmpty(WorktreeRoot.Validate(WorktreeRoot.Effective("wts", repo), repo));   // inside the repo
    }

    [Fact]
    public void A_root_reached_through_a_link_into_temp_is_refused()
    {
        using var lab = new EscapeLab();   // EscapeLab itself lives in temp
        var safeLooking = Path.Combine(Home, "tokonomy-test-link-" + Guid.NewGuid().ToString("N")[..8]);
        FsLinks.Directory(safeLooking, lab.Outside);
        try
        {
            Assert.Contains(WorktreeRoot.Validate(Path.Combine(safeLooking, "wt"), null), p => p.Contains("temp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            // Non-recursive: removes the link itself, never what it points to.
            Directory.Delete(safeLooking, recursive: false);
        }
    }
}
