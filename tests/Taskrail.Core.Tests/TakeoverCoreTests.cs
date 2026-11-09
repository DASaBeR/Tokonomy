using Taskrail.Core.Abstractions;
using Taskrail.Core.Config;
using Taskrail.Core.Model;
using Taskrail.Core.Supervision;
using Taskrail.Core.Takeover;

namespace Taskrail.Core.Tests;

public class EditBlocksTests
{
    [Fact]
    public void Parses_blocks_with_prose_around_them_and_a_file_creation()
    {
        const string reply = """
            Here is the fix.

            <<<<<<< SEARCH shop/cart.py
                def total(self, coupon=None):
                    return apply_coupon(self.subtotal(), coupon)
            =======
                def total(self, coupons=None):
                    return 0.0
            >>>>>>> REPLACE

            <<<<<<< SEARCH tests/test_new.py
            =======
            import unittest
            >>>>>>> REPLACE
            SUMMARY: done
            """;

        var edits = EditBlocks.Parse(reply);

        Assert.Equal(2, edits.Count);
        Assert.Equal("shop/cart.py", edits[0].Path);
        Assert.Equal("    def total(self, coupon=None):\n        return apply_coupon(self.subtotal(), coupon)", edits[0].Search);
        Assert.Equal("    def total(self, coupons=None):\n        return 0.0", edits[0].Replace);
        Assert.Equal("tests/test_new.py", edits[1].Path);
        Assert.Equal("", edits[1].Search);
        Assert.Equal("import unittest", edits[1].Replace);
    }

    [Fact]
    public void Handles_crlf_replies() =>
        Assert.Equal("b", Assert.Single(EditBlocks.Parse("<<<<<<< SEARCH f.py\r\na\r\n=======\r\nb\r\n>>>>>>> REPLACE\r\n")).Replace);

    [Theory]
    [InlineData("")]
    [InlineData("I could not find the problem.")]
    [InlineData("<<<<<<< SEARCH f.py\nunterminated block")]
    public void No_complete_block_means_no_edits(string reply) => Assert.Empty(EditBlocks.Parse(reply));

    [Fact]
    public void Replacement_may_contain_equals_signs()
    {
        var edit = Assert.Single(EditBlocks.Parse("<<<<<<< SEARCH f.py\nx = 1\n=======\nx == 2\nassert a === b\n>>>>>>> REPLACE"));

        Assert.Equal("x = 1", edit.Search);
        Assert.Equal("x == 2\nassert a === b", edit.Replace);
    }
}

public class LockedRegionTests
{
    [Fact]
    public void Hash_and_lines_ignore_line_endings_and_edge_newlines()
    {
        var a = new LockedRegion("f.py", "x = 1\r\ny = 2\r\n");
        var b = new LockedRegion("f.py", "x = 1\ny = 2");

        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(2, a.Lines);
        Assert.NotEqual(a.Sha256, new LockedRegion("f.py", "x = 1\ny = 3").Sha256);
    }
}

public class TakeoverTriggerTests
{
    private static readonly Supervisor Sut = new();
    private static readonly RunResult Ok = new() { ExitCode = 0, Duration = TimeSpan.FromSeconds(1) };

    private static UnitState State(int attempts = 2, int lines = 10, params string[] paths) => new()
    {
        Unit = new WorkUnit { Id = "U1", Title = "t", Spec = "s" },
        Attempts = attempts,
        DiffLines = lines,
        ChangedPaths = paths.Length == 0 ? ["src/app.py"] : paths,
    };

    private static readonly GateResult[] FailedGate = [new GateResult { Gate = "test", Status = GateStatus.Failed }];

    [Fact]
    public void Gate_failures_after_the_retry_limit_can_be_taken_over()
    {
        var d = Sut.Decide(State(), Ok, FailedGate, new Policy());

        Assert.Equal((DecisionKind.Takeover, TakeoverTrigger.GateFailures, true), (d.Kind, d.Trigger, d.CanTakeOver));
    }

    [Fact]
    public void No_changes_and_timeouts_can_be_taken_over()
    {
        var empty = State();
        empty.ChangedPaths = [];

        Assert.Equal(TakeoverTrigger.NoChanges, Sut.Decide(empty, Ok, [], new Policy()).Trigger);
        Assert.True(Sut.Decide(empty, Ok, [], new Policy()).CanTakeOver);
        var timeout = Sut.Decide(State(attempts: 1), Ok with { TimedOut = true }, [], new Policy());
        Assert.Equal((TakeoverTrigger.Timeout, true), (timeout.Trigger, timeout.CanTakeOver));
    }

    [Fact]
    public void Protected_paths_and_new_dependencies_need_a_human()
    {
        var policy = new Policy { Escalation = { ProtectedPaths = ["src/auth/**"] } };
        var protectedPath = Sut.Decide(State(paths: "src/auth/x.py"), Ok, [], policy);
        var dependency = State();
        dependency.AddedDependency = true;
        var added = Sut.Decide(dependency, Ok, [], new Policy());

        Assert.Equal((DecisionKind.Takeover, TakeoverTrigger.ProtectedPath, false), (protectedPath.Kind, protectedPath.Trigger, protectedPath.CanTakeOver));
        Assert.Equal((TakeoverTrigger.NewDependency, false), (added.Trigger, added.CanTakeOver));
    }

    [Fact]
    public void Retry_accept_and_split_are_never_takeovers()
    {
        Assert.False(Sut.Decide(State(attempts: 1), Ok, FailedGate, new Policy()).CanTakeOver);
        Assert.False(Sut.Decide(State(), Ok, [], new Policy()).CanTakeOver);
        Assert.False(Sut.Decide(State(lines: 999), Ok, FailedGate, new Policy()).CanTakeOver);
    }

    [Fact]
    public void Takeover_is_on_by_default_with_one_per_unit() => Assert.Equal(1, new Policy().Escalation.MaxTakeoversPerUnit);
}
