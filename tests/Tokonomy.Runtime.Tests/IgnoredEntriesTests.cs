using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Tests;

/// <summary>Which ignored paths a worker may not write. A folder holding only ignored leftovers must stay writable.</summary>
public class IgnoredEntriesTests
{
    private readonly GitCli _git = new(new ProcessRunner());

    [Fact]
    public async Task A_folder_holding_only_ignored_leftovers_is_not_denied_as_a_whole()
    {
        using var repo = new TestRepo((".gitignore", "node_modules/\n.next/\nbuild/\n"), ("README.md", "x\n"));
        using var tmp = new TempDir();
        var ws = await RunWorkspace.CreateAsync(_git, repo.Path, "ign1", repo.Head, default, Path.Combine(tmp.Path, "wt"));
        try
        {
            // What an earlier attempt left behind (create-next-app + npm install), after the unit was reset.
            Directory.CreateDirectory(Path.Combine(ws.Path, "frontend", "node_modules", "next"));
            File.WriteAllText(Path.Combine(ws.Path, "frontend", "node_modules", "next", "x.js"), "1");
            Directory.CreateDirectory(Path.Combine(ws.Path, "frontend", ".next"));
            File.WriteAllText(Path.Combine(ws.Path, "frontend", ".next", "cache"), "1");
            Directory.CreateDirectory(Path.Combine(ws.Path, "build"));
            File.WriteAllText(Path.Combine(ws.Path, "build", "out.bin"), "1");

            var ignored = await ws.IgnoredEntriesAsync(default);

            Assert.DoesNotContain("frontend/", ignored);                  // the folder itself stays writable
            Assert.Contains("frontend/node_modules/", ignored);           // its ignored content stays protected
            Assert.Contains("frontend/.next/", ignored);
            Assert.Contains("build/", ignored);                           // a folder an ignore rule names is one entry
        }
        finally
        {
            await ws.RemoveAsync(default);
        }
    }

    [Fact]
    public async Task A_folder_with_ordinary_content_is_listed_by_its_ignored_parts_only()
    {
        using var repo = new TestRepo((".gitignore", "*.log\n"), ("app/main.py", "x\n"));
        using var tmp = new TempDir();
        var ws = await RunWorkspace.CreateAsync(_git, repo.Path, "ign2", repo.Head, default, Path.Combine(tmp.Path, "wt"));
        try
        {
            File.WriteAllText(Path.Combine(ws.Path, "app", "debug.log"), "1");
            var ignored = await ws.IgnoredEntriesAsync(default);
            Assert.Contains("app/debug.log", ignored);
            Assert.DoesNotContain("app/", ignored);
        }
        finally
        {
            await ws.RemoveAsync(default);
        }
    }
}
