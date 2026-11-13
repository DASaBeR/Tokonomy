using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Supervision;

namespace Tokonomy.Core.Tests;

public class InterventionPolicyTests
{
    private static readonly SupervisionConfig Supervision = new();
    private static readonly EscalationConfig Escalation = new();

    private static WorkerReport Report(WorkerReportStatus status, bool decision = false) =>
        new() { Status = status, Reason = "missing_interface", Description = "IProductService does not exist", DecisionRequired = decision };

    private static SupervisionRequest? Eval(Decision decision, WorkerReport? report = null, bool repeated = false, int unitInterventions = 0,
        int takeovers = 0, bool l1Used = false, IReadOnlyList<string>? avoid = null) =>
        InterventionPolicy.Evaluate(new SupervisionContext
        {
            Decision = decision,
            Report = report,
            RepeatedFailure = repeated,
            UnitInterventions = unitInterventions,
            Takeovers = takeovers,
            InstructionCorrectionUsed = l1Used,
            AvoidTouched = avoid ?? [],
        }, Supervision, Escalation);

    private static readonly Decision Retry = new(DecisionKind.Retry, "gate(s) failed: tests");
    private static readonly Decision Limit = new(DecisionKind.Takeover, "gate(s) failed: tests (failed 2 time(s))", TakeoverTrigger.GateFailures);

    [Fact]
    public void A_blocked_worker_reaches_the_architect_with_every_level()
    {
        var call = Eval(Retry, Report(WorkerReportStatus.Blocked))!;

        Assert.Equal(SupervisionTrigger.WorkerBlocked, call.Trigger);
        Assert.Equal([InterventionLevel.None, InterventionLevel.InstructionCorrection, InterventionLevel.GuidedCorrection, InterventionLevel.Takeover], call.AllowedLevels);
    }

    [Fact]
    public void A_completed_report_that_asks_for_a_decision_also_escalates()
    {
        Assert.Equal(SupervisionTrigger.WorkerBlocked, Eval(new Decision(DecisionKind.Accept, "ok"), Report(WorkerReportStatus.Completed, decision: true))!.Trigger);
        Assert.Null(Eval(new Decision(DecisionKind.Accept, "ok"), Report(WorkerReportStatus.Completed)));
    }

    [Fact]
    public void Clarification_requests_are_their_own_trigger() =>
        Assert.Equal(SupervisionTrigger.ClarificationRequired, Eval(Retry, Report(WorkerReportStatus.ClarificationRequired))!.Trigger);

    [Theory]
    [InlineData(DecisionKind.Fail, TakeoverTrigger.None)]
    [InlineData(DecisionKind.Takeover, TakeoverTrigger.ProtectedPath)]
    [InlineData(DecisionKind.Takeover, TakeoverTrigger.NewDependency)]
    [InlineData(DecisionKind.Split, TakeoverTrigger.None)]
    public void Never_for_untrusted_attempts_human_decisions_or_oversized_units(DecisionKind kind, TakeoverTrigger trigger) =>
        Assert.Null(Eval(new Decision(kind, "x", trigger), Report(WorkerReportStatus.Blocked)));

    [Fact]
    public void A_repeated_identical_failure_escalates_early_but_never_to_a_takeover()
    {
        var call = Eval(Retry, repeated: true)!;

        Assert.Equal(SupervisionTrigger.RepeatedFailure, call.Trigger);
        Assert.DoesNotContain(InterventionLevel.Takeover, call.AllowedLevels);
        Assert.Null(Eval(Retry)); // the first failure is just retried
    }

    [Fact]
    public void The_retry_limit_allows_a_takeover_only_while_takeovers_remain()
    {
        Assert.Contains(InterventionLevel.Takeover, Eval(Limit)!.AllowedLevels);
        Assert.DoesNotContain(InterventionLevel.Takeover, Eval(Limit, takeovers: 1)!.AllowedLevels);
        Assert.DoesNotContain(InterventionLevel.InstructionCorrection, Eval(Limit, l1Used: true)!.AllowedLevels);
    }

    [Fact]
    public void Interventions_are_bounded_per_unit()
    {
        Assert.Null(Eval(Limit, unitInterventions: Supervision.MaxInterventionsPerUnit));
    }

    [Fact]
    public void Touching_an_avoided_path_is_a_scope_deviation()
    {
        var call = Eval(new Decision(DecisionKind.Accept, "all gates passed"), avoid: ["src/Auth/Jwt.cs"])!;

        Assert.Equal(SupervisionTrigger.ScopeDeviation, call.Trigger);
        Assert.Contains("src/Auth/Jwt.cs", call.Reason, StringComparison.Ordinal);
    }
}

public class SupervisionHelpersTests
{
    private static GateResult Failed(string output, params string[] failures) =>
        new() { Gate = "tests", Status = GateStatus.Failed, Output = output, NewFailures = failures };

    [Fact]
    public void The_same_failure_with_different_timings_has_the_same_signature()
    {
        Assert.Equal(
            FailureSignature.Of([Failed("FAILED test_a (0.031s)", "test_a")]),
            FailureSignature.Of([Failed("FAILED test_a (1.207s)", "test_a")]));
        Assert.NotEqual(
            FailureSignature.Of([Failed("FAILED test_a", "test_a")]),
            FailureSignature.Of([Failed("FAILED test_b", "test_b")]));
        Assert.Null(FailureSignature.Of([new GateResult { Gate = "tests", Status = GateStatus.Passed }]));
    }

    [Fact]
    public void Deviation_finds_avoided_paths_unplanned_files_and_missing_creates()
    {
        var unit = new WorkUnit
        {
            Id = "U1", Title = "t", Spec = "s",
            Modify = ["src/search.py"], Create = ["src/index.py"], Avoid = ["src/auth/"],
        };

        var d = PlanDeviation.Detect(unit, ["src/search.py", "src/auth/jwt.py", "src/util.py", "tests/test_search.py", "acceptance/test_x.py"], "acceptance");

        Assert.Equal(["src/auth/jwt.py"], d.AvoidTouched);
        Assert.Equal(["src/util.py"], d.Unplanned);
        Assert.Equal(["src/index.py"], d.MissingCreates);
        Assert.Equal(3, d.Describe().Count());
    }

    [Fact]
    public void A_unit_that_names_no_files_has_no_unplanned_files() =>
        Assert.Empty(PlanDeviation.Detect(new WorkUnit { Id = "U1", Title = "t", Spec = "s" }, ["anything.py"]).Unplanned);

    [Fact]
    public void Lesson_patterns_are_checked_only_against_added_lines()
    {
        var lesson = new Lesson { Id = "L1", Text = "filter in SQL", ForbiddenPattern = @"\.ToList\(\)\.Where" };
        var patch = """
            diff --git a/src/repo.cs b/src/repo.cs
            --- a/src/repo.cs
            +++ b/src/repo.cs
            @@ -1,2 +1,2 @@
            -var all = db.Products.ToList().Where(p => p.Name == q);
            +var all = db.Products.Where(p => p.Name == q);
            """;
        var bad = patch.Replace("+var all = db.Products.Where", "+var all = db.Products.ToList().Where", StringComparison.Ordinal);

        Assert.Empty(LessonCheck.Violations([lesson], patch));
        var violation = Assert.Single(LessonCheck.Violations([lesson], bad));
        Assert.Contains("src/repo.cs", violation, StringComparison.Ordinal);
        Assert.Contains("L1", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void Lessons_without_a_pattern_are_advice_only() =>
        Assert.Empty(LessonCheck.Violations([new Lesson { Id = "L1", Text = "be careful" }], "+anything\n"));
}

public class ReviewPolicyTests
{
    private static (bool, string) Should(string mode, ReviewSignals s) => ReviewPolicy.ShouldReview(new ReviewConfig { Mode = mode }, s);

    [Fact]
    public void Modes_off_and_always_override_the_signals()
    {
        Assert.False(Should(ReviewConfig.Off, new ReviewSignals { Risk = UnitRisk.High }).Item1);
        Assert.True(Should(ReviewConfig.Always, new ReviewSignals()).Item1);
    }

    [Fact]
    public void Routine_low_risk_units_are_not_reviewed() =>
        Assert.False(Should(ReviewConfig.Risk, new ReviewSignals { Risk = UnitRisk.Low, DiffLines = 40, ChangedFiles = 2, Attempts = 1 }).Item1);

    [Theory]
    [InlineData("high")]
    [InlineData("intervened")]
    [InlineData("deviation")]
    [InlineData("discovery")]
    [InlineData("dependency")]
    [InlineData("medium-large")]
    [InlineData("medium-retried")]
    public void Warning_signs_trigger_a_review(string sign)
    {
        var signals = sign switch
        {
            "high" => new ReviewSignals { Risk = UnitRisk.High },
            "intervened" => new ReviewSignals { Intervened = true },
            "deviation" => new ReviewSignals { Deviation = new PlanDeviation([], ["src/other.py"], []) },
            "discovery" => new ReviewSignals { ReportedDiscovery = true },
            "dependency" => new ReviewSignals { DependencyChanged = true },
            "medium-large" => new ReviewSignals { Risk = UnitRisk.Medium, DiffLines = 400 },
            _ => new ReviewSignals { Risk = UnitRisk.Medium, Attempts = 2 },
        };

        Assert.True(Should(ReviewConfig.Risk, signals).Item1);
    }
}

public class WorkerReportTests
{
    [Fact]
    public void Parses_a_structured_blocker_and_checks_its_evidence()
    {
        var report = WorkerReports.Parse("""
            {"status": "blocked", "reason": "missing_dependency", "description": "The expected interface does not exist.",
             "evidence": ["src/Services/IProductService.cs", "src/Program.cs"], "attempted_actions": ["Inspected interfaces"], "decision_required": true}
            """, p => p == "src/Program.cs")!;

        Assert.Equal(WorkerReportStatus.Blocked, report.Status);
        Assert.True(report.NeedsArchitect);
        Assert.Equal(["Inspected interfaces"], report.AttemptedActions);
        Assert.Equal(["evidence 'src/Services/IProductService.cs' does not exist in the worktree"], report.Problems);
    }

    [Theory]
    [InlineData("done", WorkerReportStatus.Completed)]
    [InlineData("needs-clarification", WorkerReportStatus.ClarificationRequired)]
    [InlineData("Unexpected Discovery", WorkerReportStatus.UnexpectedDiscovery)]
    [InlineData("FAILED", WorkerReportStatus.Failed)]
    public void Status_spellings_are_tolerated(string status, WorkerReportStatus expected) =>
        Assert.Equal(expected, WorkerReports.Parse($$"""{"status": "{{status}}"}""")!.Status);

    [Theory]
    [InlineData("""{"status": "great"}""")]
    [InlineData("""["blocked"]""")]
    [InlineData("not json")]
    [InlineData("")]
    public void Unreadable_reports_are_ignored(string text) => Assert.Null(WorkerReports.Parse(text));

    [Fact]
    public void Oversized_reports_are_ignored() =>
        Assert.Null(WorkerReports.Parse("{\"status\": \"blocked\", \"description\": \"" + new string('x', WorkerReports.MaxBytes) + "\"}"));

    [Fact]
    public void A_completed_report_is_not_a_reason_to_call_the_architect()
    {
        var report = WorkerReports.Parse("""{"status": "completed", "description": "done"}""")!;

        Assert.False(report.NeedsArchitect);
        Assert.False(report.HasDiscovery);
    }
}

public class RetryWindowTests
{
    private static readonly Supervisor Sut = new();

    [Fact]
    public void An_architect_correction_opens_a_new_retry_window()
    {
        var unit = new UnitState { Unit = new WorkUnit { Id = "U1", Title = "t", Spec = "s" }, Attempts = 3, RetryWindowStart = 2, ChangedPaths = ["a.py"], DiffLines = 3 };
        var failed = new GateResult { Gate = "tests", Status = GateStatus.Failed };
        var ok = new RunResult { ExitCode = 0, Duration = TimeSpan.Zero };

        Assert.Equal(DecisionKind.Retry, Sut.Decide(unit, ok, [failed], new Policy()).Kind);
        unit.Attempts = 4;
        Assert.Equal(DecisionKind.Takeover, Sut.Decide(unit, ok, [failed], new Policy()).Kind);
    }
}
