using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Tests;

public sealed class RepoBootstrapTests
{
    private readonly GitCli _git = new(new ProcessRunner());

    [Fact]
    public async Task A_plain_folder_becomes_a_repository_with_a_first_commit()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "index.html"), "<h1>hi</h1>");
        Directory.CreateDirectory(Path.Combine(dir.Path, "node_modules"));
        File.WriteAllText(Path.Combine(dir.Path, "node_modules", "junk.js"), "x");
        File.WriteAllText(Path.Combine(dir.Path, ".env"), "SECRET=1");

        var result = await RepoBootstrap.EnsureAsync(_git, dir.Path, default);

        Assert.True(result.InitializedRepo);
        Assert.True(result.CreatedInitialCommit);
        Assert.True(result.WroteGitIgnore);
        Assert.Equal(result.Head, await _git.HeadAsync(dir.Path, default));
        var tracked = await _git.RunAsync(dir.Path, ["ls-tree", "-r", "--name-only", "HEAD"], default);
        Assert.Contains("index.html", tracked, StringComparison.Ordinal);
        Assert.DoesNotContain("node_modules", tracked, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", tracked.Replace(".gitignore", ""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_folder_still_gets_a_commit_to_branch_from()
    {
        using var dir = new TempDir();

        var result = await RepoBootstrap.EnsureAsync(_git, dir.Path, default);

        Assert.True(result.CreatedInitialCommit);
        Assert.NotEmpty(await _git.HeadAsync(dir.Path, default));
    }

    [Fact]
    public async Task A_repository_without_commits_gets_one_and_keeps_its_own_gitignore()
    {
        using var dir = new TempDir();
        await _git.RunAsync(dir.Path, ["init", "--quiet"], default);
        File.WriteAllText(Path.Combine(dir.Path, ".gitignore"), "mine/\n");
        File.WriteAllText(Path.Combine(dir.Path, "a.txt"), "a");

        var result = await RepoBootstrap.EnsureAsync(_git, dir.Path, default);

        Assert.False(result.InitializedRepo);
        Assert.False(result.WroteGitIgnore);
        Assert.True(result.CreatedInitialCommit);
        Assert.Equal("mine/\n", File.ReadAllText(Path.Combine(dir.Path, ".gitignore")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_repository_that_already_has_commits_is_left_alone()
    {
        using var repo = new TestRepo(("a.txt", "a\n"));
        File.WriteAllText(Path.Combine(repo.Path, "uncommitted.txt"), "x");

        var result = await RepoBootstrap.EnsureAsync(_git, repo.Path, default);

        Assert.False(result.ChangedAnything);
        Assert.Equal(repo.Head, result.Head);
        Assert.Equal(repo.Head, await _git.HeadAsync(repo.Path, default));
    }
}
