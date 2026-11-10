using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Llm.Architect;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Gates;

namespace Tokonomy.Runtime.Tests;

public sealed class AcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tk-acc-" + Guid.NewGuid().ToString("N"));

    public AcceptanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly AcceptanceConfig On = new() { Cmd = ["python", "-m", "unittest", "discover", "-s", "acceptance"], Hint = "stdlib only" };

    private const string WithTests = """
        {"units":[{"id":"U1","title":"Page","spec":"Build index.html","tests":[{"path":"acceptance/test_page.py","content":"import unittest\n"}]},
                  {"id":"U2","title":"Style","spec":"Add css","depends_on":["U1"],"tests":[{"path":"acceptance/test_style.py","content":"import unittest\n"}]}]}
        """;

    [Fact]
    public async Task Planner_asks_for_tests_and_parses_them_per_unit()
    {
        var provider = new ScriptedProvider(WithTests);

        var result = await new Planner(provider, "strong").PlanAsync("landing page", "", default, On);

        Assert.Equal("acceptance/test_page.py", result.Contract.Units[0].Tests.Single().Path);
        Assert.Equal("acceptance/test_style.py", result.Contract.Units[1].Tests.Single().Path);
        Assert.Contains("acceptance/test_xxx.py", provider.Requests[0].System, StringComparison.Ordinal);
        Assert.Contains("python -m unittest discover -s acceptance", provider.Requests[0].System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Planner_does_not_mention_tests_when_the_feature_is_off()
    {
        var provider = new ScriptedProvider("""{"units":[{"id":"U1","title":"a","spec":"a"}]}""");

        await new Planner(provider, "strong").PlanAsync("task", "", default);

        Assert.DoesNotContain("REQUIRED for this project", provider.Requests[0].System, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Planner_reasks_when_tests_are_missing_or_outside_the_directory()
    {
        var none = """{"units":[{"id":"U1","title":"a","spec":"a"}]}""";
        var escape = """{"units":[{"id":"U1","title":"a","spec":"a","tests":[{"path":"../evil.py","content":"x"}]}]}""";

        var missing = new ScriptedProvider(none, WithTests);
        await new Planner(missing, "strong").PlanAsync("task", "", default, On);
        Assert.Contains("at least one executable test", missing.Requests[1].User, StringComparison.Ordinal);

        var outside = new ScriptedProvider(escape, WithTests);
        await new Planner(outside, "strong").PlanAsync("task", "", default, On);
        Assert.Contains("../evil.py", outside.Requests[1].User, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acceptance/test_a.py", true)]
    [InlineData("acceptance\\sub\\test_a.py", true)]
    [InlineData("acceptance/../x.py", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/x/acceptance/a.py", false)]
    [InlineData("other/test_a.py", false)]
    [InlineData("acceptance", false)]
    [InlineData("acceptance2/a.py", false)]
    public void Paths_must_stay_inside_the_directory(string path, bool ok) =>
        Assert.Equal(ok, AcceptancePaths.Normalize("acceptance", path) is not null);

    private static WorkUnit Unit(string id, params (string Path, string Content)[] tests) => new()
    {
        Id = id,
        Title = id,
        Spec = "s",
        Tests = tests.Select(t => new AcceptanceTestFile(t.Path, t.Content)).ToList(),
    };

    [Fact]
    public void Enforce_restores_modified_deleted_and_added_files()
    {
        var files = new AcceptanceFiles("acceptance");
        files.Add(Unit("U1", ("acceptance/test_a.py", "assert True\n"), ("acceptance/test_b.py", "assert 1\n")));
        Assert.Empty(files.Enforce(_dir));

        File.WriteAllText(Path.Combine(_dir, "acceptance", "test_a.py"), "pass\n");
        File.Delete(Path.Combine(_dir, "acceptance", "test_b.py"));
        File.WriteAllText(Path.Combine(_dir, "acceptance", "conftest.py"), "evil\n");

        var tampered = files.Enforce(_dir);

        Assert.Equal(3, tampered.Count);
        Assert.Equal("assert True\n", File.ReadAllText(Path.Combine(_dir, "acceptance", "test_a.py")));
        Assert.Equal("assert 1\n", File.ReadAllText(Path.Combine(_dir, "acceptance", "test_b.py")));
        Assert.False(File.Exists(Path.Combine(_dir, "acceptance", "conftest.py")));
        Assert.Empty(files.Enforce(_dir));
    }

    [Fact]
    public void Tests_of_later_units_are_not_written_until_their_unit_starts()
    {
        var files = new AcceptanceFiles("acceptance");
        files.Add(Unit("U1", ("acceptance/test_a.py", "a\n")));
        files.Enforce(_dir);

        Assert.True(File.Exists(Path.Combine(_dir, "acceptance", "test_a.py")));
        Assert.False(File.Exists(Path.Combine(_dir, "acceptance", "test_b.py")));

        files.Add(Unit("U2", ("acceptance/test_b.py", "b\n")));
        files.Enforce(_dir);

        Assert.True(File.Exists(Path.Combine(_dir, "acceptance", "test_b.py")));
        Assert.Equal(["acceptance/test_a.py", "acceptance/test_b.py"], files.Paths);
    }

    [Fact]
    public void Brief_tells_the_worker_the_tests_are_read_only()
    {
        var contract = new Contract { Task = "t", Units = [Unit("U1")] };

        var brief = BriefWriter.Build(contract, contract.Units[0], 1, "AGENTS.md", null, [], null, (["acceptance/test_a.py"], "python -m unittest"));

        Assert.Contains("acceptance/test_a.py", brief.Body, StringComparison.Ordinal);
        Assert.Contains("python -m unittest", brief.Body, StringComparison.Ordinal);
        Assert.Contains("Do not edit", brief.Body, StringComparison.Ordinal);
    }
}
