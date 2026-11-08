using Taskrail.Core.Config;
using Taskrail.Core.Model;
using Taskrail.Runtime.Gates;
using Taskrail.Runtime.Git;
using Taskrail.Runtime.Process;

namespace Taskrail.Runtime.Tests;

public class RunWorkspaceTests
{
    private readonly GitCli _git = new(new ProcessRunner());

    [Fact]
    public async Task Diff_commit_and_reset_lifecycle()
    {
        using var repo = new TestRepo(("app.py", "print('a')\n"));
        using var tmp = new TempDir();
        var ws = await RunWorkspace.CreateAsync(_git, repo.Path, "r1", repo.Head, default, Path.Combine(tmp.Path, "wt"));
        try
        {
            var start = await ws.HeadAsync(default);
            File.WriteAllText(Path.Combine(ws.Path, "app.py"), "print('b')\nprint('c')\n");
            File.WriteAllText(Path.Combine(ws.Path, "new.py"), "x = 1\n");
            Directory.CreateDirectory(Path.Combine(ws.Path, ".taskrail", "tmp"));
            File.WriteAllText(Path.Combine(ws.Path, ".taskrail", "tmp", "report.xml"), "<x/>");

            var diff = await ws.DiffAsync(start, default);

            Assert.Equal(["app.py", "new.py"], diff.ChangedPaths.Order(StringComparer.Ordinal));
            Assert.Equal(3, diff.AddedLines);
            Assert.Equal(1, diff.DeletedLines);
            Assert.False(diff.AddedDependency);

            var committed = await ws.CommitAsync("taskrail(U1): change", default);
            Assert.NotEqual(start, committed);
            Assert.Contains("taskrail/r1", repo.Git("branch", "--list", "taskrail/*"), StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(ws.Path, "junk.py"), "oops");
            await ws.ResetAsync(committed, default);
            Assert.False(File.Exists(Path.Combine(ws.Path, "junk.py")));
            Assert.Empty((await ws.DiffAsync(committed, default)).ChangedPaths);
        }
        finally
        {
            await ws.RemoveAsync(default);
        }

        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "wt")));
    }

    [Fact]
    public async Task Detects_added_npm_dependency()
    {
        using var repo = new TestRepo(("package.json", """{ "dependencies": { "a": "1" } }"""));
        using var tmp = new TempDir();
        var ws = await RunWorkspace.CreateAsync(_git, repo.Path, "r2", repo.Head, default, Path.Combine(tmp.Path, "wt"));
        try
        {
            File.WriteAllText(Path.Combine(ws.Path, "package.json"), """{ "dependencies": { "a": "2" } }""");
            Assert.False((await ws.DiffAsync(repo.Head, default)).AddedDependency);

            File.WriteAllText(Path.Combine(ws.Path, "package.json"), """{ "dependencies": { "a": "2", "left-pad": "1" } }""");
            Assert.True((await ws.DiffAsync(repo.Head, default)).AddedDependency);
        }
        finally
        {
            await ws.RemoveAsync(default);
        }
    }
}

public class DependencyDetectorTests
{
    private static Task<bool> Detect(string file, string addedLine) =>
        DependencyDetector.DetectAsync([file], $"+++ b/{file}\n+{addedLine}\n", _ => Task.FromResult<string?>(null), _ => null);

    [Theory]
    [InlineData("App.csproj", "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", true)]
    [InlineData("App.csproj", "    <Compile Include=\"x.cs\" />", false)]
    [InlineData("requirements.txt", "requests==2.32.0", true)]
    [InlineData("requirements.txt", "# just a comment", false)]
    [InlineData("pyproject.toml", "    \"httpx>=0.27\",", true)]
    [InlineData("pyproject.toml", "name = \"demo\"", false)]
    [InlineData("go.mod", "\tgithub.com/pkg/errors v0.9.1", true)]
    [InlineData("poetry.lock", "anything", true)]
    public async Task Detects_dependency_additions(string file, string line, bool expected) =>
        Assert.Equal(expected, await Detect(file, line));
}

public class GateEvaluationTests
{
    private static GateRun Run(bool passed, params string[] failing) =>
        new("test", passed, false, failing.Length == 0 && passed ? new HashSet<string>() : failing.ToHashSet(), "output");

    private static GateRun RunNoReport(bool passed) => new("lint", passed, false, null, "lint output");

    [Fact]
    public void Pass_is_pass() => Assert.Equal(GateStatus.Passed, CommandGate.Evaluate(Run(true), Run(false, "a")).Status);

    [Fact]
    public void Failure_on_green_baseline_blocks()
    {
        var r = CommandGate.Evaluate(Run(false, "t1"), Run(true));

        Assert.Equal(GateStatus.Failed, r.Status);
        Assert.Equal(["t1"], r.NewFailures);
    }

    [Fact]
    public void Same_failures_as_baseline_are_preexisting() =>
        Assert.Equal(GateStatus.PreExisting, CommandGate.Evaluate(Run(false, "old"), Run(false, "old")).Status);

    [Fact]
    public void New_failures_on_red_baseline_block()
    {
        var r = CommandGate.Evaluate(Run(false, "old", "new"), Run(false, "old"));

        Assert.Equal(GateStatus.Failed, r.Status);
        Assert.Equal(["new"], r.NewFailures);
    }

    [Fact]
    public void Missing_report_when_baseline_had_one_blocks() =>
        Assert.Equal(GateStatus.Failed, CommandGate.Evaluate(RunNoReport(false), Run(false, "old")).Status);

    [Fact]
    public void Red_baseline_without_reports_is_preexisting() =>
        Assert.Equal(GateStatus.PreExisting, CommandGate.Evaluate(RunNoReport(false), RunNoReport(false)).Status);

    [Fact]
    public async Task Command_gate_runs_and_reads_junit_report()
    {
        using var tmp = new TempDir();
        tmp.File("src.txt", "ok");
        var junit = """
            <testsuites><testsuite name="s">
              <testcase classname="tests.test_a" name="test_ok"/>
              <testcase classname="tests.test_a" name="test_bad"><failure message="boom"/></testcase>
            </testsuite></testsuites>
            """;
        tmp.File("fixture.xml", junit);
        var gate = new CommandGate(
            new GateConfig
            {
                Name = "test",
                Cmd = Shell.Cmd("cp fixture.xml report.xml; exit 1", "Copy-Item fixture.xml report.xml; exit 1"),
                Report = "report.xml",
            },
            new ProcessRunner(),
            EnvironmentBuilder.ForGate([]));

        var run = await gate.ExecuteAsync(tmp.Path, null, default);

        Assert.False(run.Passed);
        Assert.Equal(["tests.test_a::test_bad"], run.FailingTests!);
    }

    [Fact]
    public async Task Missing_gate_tool_is_a_failure_not_a_crash()
    {
        using var tmp = new TempDir();
        var gate = new CommandGate(new GateConfig { Cmd = ["taskrail-no-such-linter"] }, new ProcessRunner(), EnvironmentBuilder.ForGate([]));

        var run = await gate.ExecuteAsync(tmp.Path, null, default);

        Assert.False(run.Passed);
        Assert.Contains("not found", run.OutputTail, StringComparison.Ordinal);
    }
}

public class TestReportParserTests
{
    [Fact]
    public void Parses_trx_failures()
    {
        using var tmp = new TempDir();
        var path = tmp.File("r.trx", """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
              <UnitTestResult testName="Ns.A.Passes" outcome="Passed"/>
              <UnitTestResult testName="Ns.A.Fails" outcome="Failed"/>
            </Results></TestRun>
            """);

        Assert.Equal(["Ns.A.Fails"], TestReportParser.TryParseFailures(path)!);
    }

    [Fact]
    public void Missing_or_corrupt_report_is_null()
    {
        using var tmp = new TempDir();
        Assert.Null(TestReportParser.TryParseFailures(Path.Combine(tmp.Path, "none.xml")));
        Assert.Null(TestReportParser.TryParseFailures(tmp.File("bad.xml", "<not closed")));
    }
}
