using Tokonomy.Core.Model;
using Tokonomy.Core.Takeover;
using Tokonomy.Llm.Architect;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Gates;
using Tokonomy.Runtime.Takeover;
using YamlDotNet.Serialization;

namespace Tokonomy.Runtime.Tests;

public class EditApplierTests
{
    private static EditBlock Edit(string path, string search, string replace) => new(path, search, replace);

    [Fact]
    public void Replaces_a_unique_match_and_reports_the_file()
    {
        using var tmp = new TempDir();
        var file = tmp.File("shop/cart.py", "def total():\n    return 1\n\ndef other():\n    return 2\n");

        var result = EditApplier.Apply(tmp.Path, [Edit("shop/cart.py", "    return 1", "    return 42")]);

        Assert.True(result.Ok);
        Assert.Equal(["shop/cart.py"], result.Files);
        Assert.Equal("def total():\n    return 42\n\ndef other():\n    return 2\n", File.ReadAllText(file));
    }

    [Fact]
    public void Applies_several_blocks_to_the_same_file_in_order()
    {
        using var tmp = new TempDir();
        var file = tmp.File("a.py", "x = 1\ny = 2\n");

        var result = EditApplier.Apply(tmp.Path, [Edit("a.py", "x = 1", "x = 10"), Edit("a.py", "y = 2", "y = x + 1")]);

        Assert.True(result.Ok);
        Assert.Equal("x = 10\ny = x + 1\n", File.ReadAllText(file));
    }

    [Fact]
    public void Ambiguous_search_fails_and_nothing_is_written()
    {
        using var tmp = new TempDir();
        var a = tmp.File("a.py", "ok = 1\n");
        var b = tmp.File("b.py", "return 0\nreturn 0\n");

        var result = EditApplier.Apply(tmp.Path, [Edit("a.py", "ok = 1", "ok = 2"), Edit("b.py", "return 0", "return 1")]);

        Assert.False(result.Ok);
        Assert.Contains("matched 2 times", Assert.Single(result.Errors), StringComparison.Ordinal);
        Assert.Equal("ok = 1\n", File.ReadAllText(a));
        Assert.Equal("return 0\nreturn 0\n", File.ReadAllText(b));
    }

    [Fact]
    public void Missing_text_and_missing_files_are_reported()
    {
        using var tmp = new TempDir();
        tmp.File("a.py", "x = 1\n");

        var result = EditApplier.Apply(tmp.Path, [Edit("a.py", "y = 2", "y = 3"), Edit("ghost.py", "z", "w")]);

        Assert.False(result.Ok);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("not found", result.Errors[0], StringComparison.Ordinal);
        Assert.Contains("file not found", result.Errors[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Tolerates_trailing_whitespace_and_crlf_when_the_match_is_unique()
    {
        using var tmp = new TempDir();
        var file = tmp.File("a.py", "def f():  \r\n    return 1\t\r\n\r\ndef g():\r\n    pass\r\n");

        var result = EditApplier.Apply(tmp.Path, [Edit("a.py", "def f():\n    return 1", "def f():\n    return 2")]);

        Assert.True(result.Ok);
        Assert.Equal("def f():\r\n    return 2\r\n\r\ndef g():\r\n    pass\r\n", File.ReadAllText(file));
    }

    [Fact]
    public void Tolerant_match_still_refuses_ambiguity()
    {
        using var tmp = new TempDir();
        tmp.File("a.py", "x = 1 \nx = 1  \n");

        var result = EditApplier.Apply(tmp.Path, [Edit("a.py", "x = 1", "x = 2")]);

        Assert.True(result.Ok is false || File.ReadAllText(Path.Combine(tmp.Path, "a.py")).Contains("x = 1", StringComparison.Ordinal));
        Assert.False(result.Ok);
    }

    [Fact]
    public void Empty_search_creates_a_file_with_a_trailing_newline()
    {
        using var tmp = new TempDir();

        var result = EditApplier.Apply(tmp.Path, [Edit("tests/test_new.py", "", "import unittest")]);

        Assert.True(result.Ok);
        Assert.Equal("import unittest\n", File.ReadAllText(Path.Combine(tmp.Path, "tests", "test_new.py")));
    }

    [Theory]
    [InlineData("../outside.py")]
    [InlineData("a/../../outside.py")]
    [InlineData(".git/config")]
    [InlineData(".tokonomy/policy.yaml")]
    [InlineData("C:/Windows/x.py")]
    [InlineData("")]
    public void Refuses_paths_that_escape_the_worktree_or_touch_tokonomy_state(string path)
    {
        using var tmp = new TempDir();
        tmp.File("a.py", "x");

        var result = EditApplier.Apply(tmp.Path, [Edit(path, "", "pwned")]);

        Assert.False(result.Ok);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "..", "outside.py")));
    }

    [Fact]
    public void A_leading_slash_means_repo_relative_and_never_escapes_the_worktree()
    {
        using var tmp = new TempDir();

        var result = EditApplier.Apply(tmp.Path, [Edit("/etc/notes.txt", "", "hello")]);

        Assert.True(result.Ok);
        Assert.Equal(["etc/notes.txt"], result.Files);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "etc", "notes.txt")));
    }

    [Fact]
    public void No_blocks_is_an_error() => Assert.False(EditApplier.Apply(Path.GetTempPath(), []).Ok);
}

public class LockedRegionGateTests
{
    private static readonly LockedRegion Region = new("shop/cart.py", "    def total(self):\n        return 42");

    [Fact]
    public void Passes_while_the_text_is_present_whatever_surrounds_it()
    {
        using var tmp = new TempDir();
        tmp.File("shop/cart.py", "# new header\nclass Cart:\n    def total(self):\n        return 42\n\n    def extra(self):\n        pass\n");

        Assert.Equal(GateStatus.Passed, LockedRegionGate.Evaluate(tmp.Path, [Region]).Status);
    }

    [Fact]
    public void Passes_for_crlf_files()
    {
        using var tmp = new TempDir();
        tmp.File("shop/cart.py", "class Cart:\r\n    def total(self):\r\n        return 42\r\n");

        Assert.Equal(GateStatus.Passed, LockedRegionGate.Evaluate(tmp.Path, [Region]).Status);
    }

    [Theory]
    [InlineData("class Cart:\n    def total(self):\n        return 43\n")]
    [InlineData("class Cart:\n    pass\n")]
    public void Fails_when_the_locked_text_changed_and_names_the_file(string content)
    {
        using var tmp = new TempDir();
        tmp.File("shop/cart.py", content);

        var result = LockedRegionGate.Evaluate(tmp.Path, [Region]);

        Assert.Equal(GateStatus.Failed, result.Status);
        Assert.True(result.Blocking);
        Assert.Contains("shop/cart.py", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_when_the_file_was_deleted()
    {
        using var tmp = new TempDir();

        Assert.Equal(GateStatus.Failed, LockedRegionGate.Evaluate(tmp.Path, [Region]).Status);
    }

    [Fact]
    public void No_locks_always_passes()
    {
        using var tmp = new TempDir();

        Assert.Equal(GateStatus.Passed, LockedRegionGate.Evaluate(tmp.Path, []).Status);
    }
}

public class HandoffTests
{
    private static Handoff Sample() => new()
    {
        UnitId = "U1",
        UnitTitle = "Limiter",
        Files = ["shop/cart.py"],
        Summary = "Moved state into the instance",
        Why = "The worker used module-level state",
        Locked = [new LockedRegion("shop/cart.py", "def total(self):\n    return 42")],
        MustFollow = ["R1: no module-level mutable state"],
    };

    [Fact]
    public void Yaml_has_the_brief_shape_and_round_trips()
    {
        var yaml = HandoffWriter.ToYaml(Sample(), "U2: Wire it in");

        var doc = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);
        Assert.Equal("U1", doc["unit"]);
        var done = (Dictionary<object, object>)doc["done_by_architect"];
        Assert.Equal("Moved state into the instance", done["summary"]);
        Assert.Equal("The worker used module-level state", done["why"]);
        Assert.Equal("shop/cart.py", ((List<object>)done["files"])[0]);
        var locked = (Dictionary<object, object>)((List<object>)doc["locked"])[0];
        Assert.Equal("shop/cart.py", locked["path"]);
        Assert.Equal("2", locked["lines"]);
        Assert.Equal(64, ((string)locked["sha256"]).Length);
        var next = (Dictionary<object, object>)doc["worker_next"];
        Assert.Equal("U2: Wire it in", next["resume_from"]);
        Assert.Equal("R1: no module-level mutable state", ((List<object>)next["must_follow"])[0]);
    }

    [Fact]
    public void Resume_point_is_omitted_for_the_last_unit() =>
        Assert.DoesNotContain("resume_from", HandoffWriter.ToYaml(Sample(), null), StringComparison.Ordinal);

    [Fact]
    public void Worker_brief_shows_the_handoff_and_the_locked_text()
    {
        var contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "a", Spec = "s" }, new WorkUnit { Id = "U2", Title = "b", Spec = "s2" }] };

        var brief = BriefWriter.Build(contract, contract.Units[1], 1, "AGENTS.md", null, [contract.Units[0]], [Sample()]);

        Assert.Contains("## Handoff from the architect", brief.Body, StringComparison.Ordinal);
        Assert.Contains("Moved state into the instance", brief.Body, StringComparison.Ordinal);
        Assert.Contains("The worker used module-level state", brief.Body, StringComparison.Ordinal);
        Assert.Contains("an automated check fails if these lines change", brief.Body, StringComparison.Ordinal);
        Assert.Contains("    return 42", brief.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Brief_has_no_handoff_section_without_takeovers()
    {
        var contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "a", Spec = "s" }] };

        Assert.DoesNotContain("Handoff", BriefWriter.Build(contract, contract.Units[0], 1, "AGENTS.md", null, []).Body, StringComparison.Ordinal);
    }
}

public class TakeoverPromptTests
{
    private static readonly Contract Contract = new()
    {
        Task = "Add rate limiting",
        Units = [new WorkUnit { Id = "U1", Title = "Limiter", Spec = "Add a RateLimiter", Acceptance = ["tests pass"] }],
        Rules = [new Rule("R1", "no module-level state")],
        DontList = ["add dependencies"],
    };

    [Fact]
    public void Prompt_carries_the_minimum_context()
    {
        var prompt = TakeoverPrompt.Build(
            Contract, Contract.Units[0], 2, "gate(s) failed: test", "diff --git a/x b/x", "FAILED test_x",
            new Dictionary<string, string> { ["shop/limiter.py"] = "class RateLimiter: ..." }, ["shop/limiter.py", "README.md"],
            [new LockedRegion("shop/cart.py", "locked text")]);

        foreach (var expected in new[]
        {
            "Add rate limiting", "R1: no module-level state", "add dependencies", "Add a RateLimiter", "tests pass",
            "gate(s) failed: test", "diff --git a/x b/x", "FAILED test_x", "class RateLimiter", "locked text", "README.md",
        })
        {
            Assert.Contains(expected, prompt, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("could not be applied", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Reask_prompt_includes_what_went_wrong() =>
        Assert.Contains("SEARCH matched 2 times",
            TakeoverPrompt.Build(Contract, Contract.Units[0], 2, "r", "", "", new Dictionary<string, string>(), [], [], "SEARCH matched 2 times"),
            StringComparison.Ordinal);

    [Fact]
    public void No_diff_is_described_as_no_changes() =>
        Assert.Contains("(no changes were made)", TakeoverPrompt.Build(Contract, Contract.Units[0], 2, "r", "", "", new Dictionary<string, string>(), [], []), StringComparison.Ordinal);

    [Fact]
    public void Long_failure_output_keeps_the_tail()
    {
        var output = new string('a', 10_000) + "THE_REAL_ERROR";

        var prompt = TakeoverPrompt.Build(Contract, Contract.Units[0], 2, "r", "", output, new Dictionary<string, string>(), [], []);

        Assert.Contains("THE_REAL_ERROR", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length < 8_000);
    }

    [Fact]
    public void Parses_edits_and_the_handoff_note()
    {
        var reply = TakeoverPrompt.Parse("<<<<<<< SEARCH a.py\nx\n=======\ny\n>>>>>>> REPLACE\nSUMMARY: Fixed x\nWHY: It was y\n");

        Assert.Single(reply.Edits);
        Assert.Equal("Fixed x", reply.Summary);
        Assert.Equal("It was y", reply.Why);
    }

    [Fact]
    public void Note_is_optional()
    {
        var reply = TakeoverPrompt.Parse("<<<<<<< SEARCH a.py\nx\n=======\ny\n>>>>>>> REPLACE");

        Assert.Null(reply.Summary);
        Assert.Null(reply.Why);
    }
}
