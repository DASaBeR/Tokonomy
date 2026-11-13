using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;

namespace Tokonomy.Core.Tests;

public class PlanManagerTests
{
    private static WorkUnit U(string id, params string[] deps) => new()
    {
        Id = id,
        Title = id,
        Spec = "do " + id,
        DependsOn = deps,
        Steps = ["step 1"],
        Acceptance = [$"{id} works"],
    };

    private static Contract Plan(params WorkUnit[] units) => new() { Task = "t", Units = units, SchemaVersion = 2 };

    private static PlanUpdate Update(PlanManager plan, params PlanOperation[] ops) =>
        new() { BaseRevision = plan.Revision, Source = "supervision", Reason = "test", Operations = ops };

    [Fact]
    public void Starts_at_revision_one_and_hands_out_units_in_dependency_order()
    {
        var plan = new PlanManager(Plan(U("U2", "U1"), U("U1")));

        Assert.Equal(1, plan.Revision);
        Assert.Equal("U1", plan.NextPending()!.Id);
        plan.MarkRunning("U1");
        Assert.Null(plan.NextPending()); // U2 waits for U1
        plan.MarkAccepted("U1");
        Assert.Equal("U2", plan.NextPending()!.Id);
    }

    [Fact]
    public void Updates_create_a_revision_with_before_and_after_values_and_never_touch_the_original()
    {
        var plan = new PlanManager(Plan(U("U1"), U("U2", "U1")));

        var result = plan.Apply(Update(plan, new PlanOperation
        {
            Op = PlanOps.UpdateUnit,
            Unit = "U2",
            Steps = ["filter in the database"],
            Constraints = ["never load every row"],
        }));

        Assert.True(result.Applied, string.Join("; ", result.Errors));
        Assert.Equal(2, plan.Revision);
        Assert.Equal(["step 1", "filter in the database"], plan.Unit("U2").Steps);
        Assert.Equal(["never load every row"], plan.Unit("U2").Constraints);
        Assert.Empty(plan.Original.Units[1].Constraints);
        var revision = Assert.Single(plan.Revisions);
        Assert.Equal(2, revision.Number);
        Assert.Contains(revision.Changes, c => c.UnitId == "U2" && c.Field == "constraints" && c.Before == "[]");
    }

    [Fact]
    public void Completed_units_are_immutable_unless_reopened_explicitly()
    {
        var plan = new PlanManager(Plan(U("U1"), U("U2", "U1")));
        plan.MarkAccepted("U1");

        var result = plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.UpdateUnit, Unit = "U1", Constraints = ["x"] }));

        Assert.False(result.Applied);
        Assert.Contains("immutable", result.Errors.Single(), StringComparison.Ordinal);
        Assert.Equal(1, plan.Revision);

        var reopened = plan.Reopen("U1", "the user asked to redo it");
        Assert.True(reopened.Applied);
        Assert.True(reopened.Record!.RequiresHumanApproval);
        Assert.Equal(UnitProgress.Pending, plan.ProgressOf("U1"));
    }

    [Fact]
    public void A_stale_update_is_rejected()
    {
        var plan = new PlanManager(Plan(U("U1"), U("U2")));
        var stale = Update(plan, new PlanOperation { Op = PlanOps.AddDont, Text = "a" });
        Assert.True(plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.AddDont, Text = "b" })).Applied);

        var result = plan.Apply(stale);

        Assert.False(result.Applied);
        Assert.Contains("stale", result.Errors.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Concurrent_updates_on_the_same_revision_apply_exactly_once()
    {
        var plan = new PlanManager(Plan(U("U1")));
        var updates = Enumerable.Range(0, 16).Select(i => Update(plan, new PlanOperation { Op = PlanOps.AddDont, Text = $"rule {i}" })).ToList();

        var results = updates.AsParallel().Select(plan.Apply).ToList();

        Assert.Equal(1, results.Count(r => r.Applied));
        Assert.Equal(2, plan.Revision);
        Assert.Single(plan.Current.DontList);
    }

    [Fact]
    public void An_update_is_all_or_nothing()
    {
        var plan = new PlanManager(Plan(U("U1"), U("U2")));

        var result = plan.Apply(Update(plan,
            new PlanOperation { Op = PlanOps.AddDont, Text = "valid" },
            new PlanOperation { Op = PlanOps.AddDependency, Unit = "U1", DependsOn = ["U9"] }));

        Assert.False(result.Applied);
        Assert.Empty(plan.Current.DontList);
        Assert.Equal(1, plan.Revision);
    }

    [Fact]
    public void A_revision_that_would_create_a_cycle_is_rejected()
    {
        var plan = new PlanManager(Plan(U("U1"), U("U2", "U1")));

        var result = plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.AddDependency, Unit = "U1", DependsOn = ["U2"] }));

        Assert.False(result.Applied);
        Assert.Contains("cycle", string.Join(" ", result.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".tokonomy/policy.yaml")]
    [InlineData("acceptance/test_x.py")]
    [InlineData("secrets/key.pem")]
    [InlineData("../outside.txt")]
    public void Plan_updates_never_target_protected_paths(string path)
    {
        var plan = new PlanManager(Plan(U("U1")), ["secrets/**", "acceptance/**"]);

        var result = plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.UpdateUnit, Unit = "U1", Modify = [path] }));

        Assert.False(result.Applied);
    }

    [Fact]
    public void New_units_can_be_added_and_ordered_before_pending_work()
    {
        var plan = new PlanManager(Plan(U("U1"), U("U2", "U1")));
        plan.MarkAccepted("U1");

        var result = plan.Apply(Update(plan,
            new PlanOperation { Op = PlanOps.AddUnit, NewUnit = U("U1b", "U1") with { Create = ["src/repo.py"] } },
            new PlanOperation { Op = PlanOps.AddDependency, Unit = "U2", DependsOn = ["U1b"] }));

        Assert.True(result.Applied, string.Join("; ", result.Errors));
        Assert.Equal("U1b", plan.NextPending()!.Id);
        Assert.Contains("src/repo.py", plan.Unit("U1b").Files);
    }

    [Fact]
    public void Units_added_during_a_run_cannot_carry_acceptance_tests()
    {
        var plan = new PlanManager(Plan(U("U1")));
        var unit = U("U2") with { Tests = [new AcceptanceTestFile("acceptance/test_x.py", "assert False")] };

        Assert.False(plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.AddUnit, NewUnit = unit })).Applied);
    }

    [Fact]
    public void Removing_a_unit_needs_a_reason_and_repoints_its_dependents()
    {
        var plan = new PlanManager(Plan(U("U1"), U("U2", "U1"), U("U3", "U2")));

        Assert.False(plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.RemoveUnit, Unit = "U2" })).Applied);
        var result = plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.RemoveUnit, Unit = "U2", Text = "U1 already does it" }));

        Assert.True(result.Applied);
        Assert.Equal(["U1"], plan.Unit("U3").DependsOn);
        Assert.Equal(UnitProgress.Removed, plan.ProgressOf("U2"));
    }

    [Fact]
    public void A_unit_with_acceptance_tests_cannot_be_removed()
    {
        var plan = new PlanManager(Plan(U("U1") with { Tests = [new AcceptanceTestFile("acceptance/test_a.py", "x")] }));

        Assert.False(plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.RemoveUnit, Unit = "U1", Text = "unneeded" })).Applied);
    }

    [Fact]
    public void Splitting_preserves_dependencies_tests_and_acceptance_coverage()
    {
        var tests = new AcceptanceTestFile("acceptance/test_u2.py", "check");
        var plan = new PlanManager(Plan(U("U1"), U("U2", "U1") with { Tests = [tests], Acceptance = ["search works", "pages work"] }, U("U3", "U2")));
        plan.MarkAccepted("U1");
        plan.MarkRunning("U2");

        var result = plan.Apply(Update(plan, new PlanOperation
        {
            Op = PlanOps.SplitUnit,
            Unit = "U2",
            Parts = [new WorkUnit { Id = "U2a", Title = "query", Spec = "query", Acceptance = ["search works"] }, new WorkUnit { Id = "U2b", Title = "pages", Spec = "pages" }],
        }));

        Assert.True(result.Applied, string.Join("; ", result.Errors));
        Assert.Equal(["U1", "U2a", "U2b", "U3"], plan.Current.Units.Select(u => u.Id));
        Assert.Equal(["U1"], plan.Unit("U2a").DependsOn);
        Assert.Equal(["U1", "U2a"], plan.Unit("U2b").DependsOn);
        Assert.Equal([tests], plan.Unit("U2b").Tests);
        Assert.Equal(["search works", "pages work"], plan.Unit("U2b").Acceptance);
        Assert.Equal(["U2b"], plan.Unit("U3").DependsOn);
        Assert.Equal(UnitProgress.Superseded, plan.ProgressOf("U2"));
        Assert.Equal("U2a", plan.NextPending()!.Id);
        Assert.Equal("U2", plan.Unit("U2a").SplitFrom);
    }

    [Fact]
    public void A_split_part_is_never_split_again()
    {
        var plan = new PlanManager(Plan(U("U1")));
        Assert.True(plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.SplitUnit, Unit = "U1", Parts = [U("U1a"), U("U1b")] })).Applied);

        var again = plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.SplitUnit, Unit = "U1a", Parts = [U("U1c"), U("U1d")] }));

        Assert.False(again.Applied);
        Assert.Contains("not split again", again.Errors.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Lessons_reach_the_units_in_their_scope_including_split_parts()
    {
        var plan = new PlanManager(Plan(U("U1"), U("U2", "U1"), U("U3", "U1")));

        var result = plan.Apply(Update(plan,
            new PlanOperation { Op = PlanOps.AddLesson, Unit = "U1", Text = "filter in SQL", Scope = "units", AppliesTo = ["U2"] },
            new PlanOperation { Op = PlanOps.AddLesson, Unit = "U1", Text = "no global state", Scope = "global", ForbiddenPattern = @"^\s*global\b" }));
        Assert.True(result.Applied, string.Join("; ", result.Errors));
        Assert.True(plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.SplitUnit, Unit = "U2", Parts = [U("U2a"), U("U2b")] })).Applied);

        Assert.Equal(["L1", "L2"], plan.LessonsFor("U2a").Select(l => l.Id));
        Assert.Equal(["L2"], plan.LessonsFor("U3").Select(l => l.Id));
        Assert.Equal(2, plan.Lessons[0].Revision);
    }

    [Theory]
    [InlineData("units", null, "(unclosed")]
    [InlineData("everywhere", null, null)]
    public void Invalid_lessons_are_rejected(string scope, string? appliesTo, string? pattern)
    {
        var plan = new PlanManager(Plan(U("U1")));

        var result = plan.Apply(Update(plan, new PlanOperation
        {
            Op = PlanOps.AddLesson,
            Text = "x",
            Scope = scope,
            AppliesTo = appliesTo is null ? [] : [appliesTo],
            ForbiddenPattern = pattern,
            Unit = "U1",
        }));

        Assert.False(result.Applied);
    }

    [Fact]
    public void The_revision_limit_stops_endless_revision_loops()
    {
        var plan = new PlanManager(Plan(U("U1")), maxRevisions: 2);

        Assert.True(plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.AddDont, Text = "a" })).Applied);
        Assert.True(plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.AddDont, Text = "b" })).Applied);
        var third = plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.AddDont, Text = "c" }));

        Assert.False(third.Applied);
        Assert.Contains("revision limit", third.Errors.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_restored_state_resumes_running_units_as_pending()
    {
        var plan = new PlanManager(Plan(U("U1"), U("U2", "U1")));
        plan.MarkAccepted("U1");
        plan.MarkRunning("U2");
        Assert.True(plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.AddRule, Text = "keep it small" })).Applied);

        var restored = PlanManager.Restore(plan.Snapshot());

        Assert.Equal(2, restored.Revision);
        Assert.Equal("U2", restored.NextPending()!.Id);
        Assert.Equal("R1: keep it small", $"{restored.Current.Rules[0].Id}: {restored.Current.Rules[0].Text}");
    }

    [Fact]
    public void The_running_unit_can_receive_corrections()
    {
        var plan = new PlanManager(Plan(U("U1")));
        plan.MarkRunning("U1");

        var result = plan.Apply(Update(plan, new PlanOperation { Op = PlanOps.UpdateUnit, Unit = "U1", Corrections = ["use the repository, not raw SQL"] }));

        Assert.True(result.Applied);
        Assert.Equal(["use the repository, not raw SQL"], plan.Unit("U1").Corrections);
    }
}
