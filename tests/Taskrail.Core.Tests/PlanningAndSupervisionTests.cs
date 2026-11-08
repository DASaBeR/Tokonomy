using Taskrail.Core.Abstractions;
using Taskrail.Core.Config;
using Taskrail.Core.Model;
using Taskrail.Core.Planning;
using Taskrail.Core.Supervision;

namespace Taskrail.Core.Tests;

public class ContractValidatorTests
{
    private static WorkUnit U(string id, params string[] deps) => new() { Id = id, Title = id, Spec = "do " + id, DependsOn = deps };

    private static Contract C(params WorkUnit[] units) => new() { Task = "t", Units = units };

    [Fact]
    public void Valid_dag_orders_dependencies_first_and_keeps_stable_order()
    {
        var contract = C(U("U3", "U1"), U("U1"), U("U2"), U("U4", "U3", "U2"));

        Assert.Empty(ContractValidator.Validate(contract));
        Assert.Equal(["U1", "U3", "U2", "U4"], ContractValidator.Order(contract).Select(u => u.Id));
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("unknown")]
    [InlineData("self")]
    [InlineData("duplicate")]
    [InlineData("badid")]
    [InlineData("empty")]
    public void Invalid_contracts_are_rejected(string kind)
    {
        var contract = kind switch
        {
            "cycle" => C(U("A", "B"), U("B", "A")),
            "unknown" => C(U("A", "Z")),
            "self" => C(U("A", "A")),
            "duplicate" => C(U("A"), U("A")),
            "badid" => C(U("1bad")),
            _ => C(),
        };

        Assert.NotEmpty(ContractValidator.Validate(contract));
    }

    [Fact]
    public void Order_throws_on_cycle() =>
        Assert.Throws<InvalidOperationException>(() => ContractValidator.Order(C(U("A", "B"), U("B", "A"))));
}

public class GlobTests
{
    [Theory]
    [InlineData("src/auth/**", "src/auth/token.py", true)]
    [InlineData("src/auth/**", "src/auth/deep/x.py", true)]
    [InlineData("src/auth/**", "src/authz/x.py", false)]
    [InlineData("migrations/**", "migrations/0001.sql", true)]
    [InlineData("*.lock", "sub/dir/poetry.lock", true)]
    [InlineData("src/*.cs", "src/a/b.cs", false)]
    [InlineData("src/*.cs", "src/b.cs", true)]
    [InlineData("**/secrets/*", "a/b/secrets/key.txt", true)]
    [InlineData("config/app?.json", "config/app1.json", true)]
    public void Matches_paths(string pattern, string path, bool expected) => Assert.Equal(expected, Glob.IsMatch(pattern, path));
}

public class SupervisorTests
{
    private static readonly Supervisor Sut = new();
    private static readonly RunResult Ok = new() { ExitCode = 0, Duration = TimeSpan.FromSeconds(1) };

    private static Policy Policy(Action<EscalationConfig>? configure = null)
    {
        var p = new Policy();
        configure?.Invoke(p.Escalation);
        return p;
    }

    private static UnitState State(int attempts = 1, int lines = 10, params string[] paths) => new()
    {
        Unit = new WorkUnit { Id = "U1", Title = "t", Spec = "s" },
        Attempts = attempts,
        DiffLines = lines,
        ChangedPaths = paths.Length == 0 ? ["src/app.py"] : paths,
    };

    private static GateResult Gate(GateStatus status) => new() { Gate = "test", Status = status };

    [Fact]
    public void Accepts_when_all_gates_pass() =>
        Assert.Equal(DecisionKind.Accept, Sut.Decide(State(), Ok, [Gate(GateStatus.Passed)], Policy()).Kind);

    [Fact]
    public void Preexisting_failures_do_not_block() =>
        Assert.Equal(DecisionKind.Accept, Sut.Decide(State(), Ok, [Gate(GateStatus.PreExisting)], Policy()).Kind);

    [Fact]
    public void Retries_on_first_gate_failure_then_escalates_at_limit()
    {
        Assert.Equal(DecisionKind.Retry, Sut.Decide(State(attempts: 1), Ok, [Gate(GateStatus.Failed)], Policy()).Kind);
        Assert.Equal(DecisionKind.Takeover, Sut.Decide(State(attempts: 2), Ok, [Gate(GateStatus.Failed)], Policy()).Kind);
    }

    [Fact]
    public void Oversized_diff_splits_before_anything_else() =>
        Assert.Equal(DecisionKind.Split, Sut.Decide(State(lines: 401, paths: "src/auth/x.py"), Ok, [Gate(GateStatus.Failed)],
            Policy(e => e.ProtectedPaths = ["src/auth/**"])).Kind);

    [Fact]
    public void Protected_path_triggers_takeover_even_when_gates_pass()
    {
        var d = Sut.Decide(State(paths: "src/auth/token.py"), Ok, [Gate(GateStatus.Passed)], Policy(e => e.ProtectedPaths = ["src/auth/**"]));

        Assert.Equal(DecisionKind.Takeover, d.Kind);
        Assert.Contains("src/auth/token.py", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Taskrail_config_is_always_protected() =>
        Assert.Equal(DecisionKind.Takeover, Sut.Decide(State(paths: ".taskrail/policy.yaml"), Ok, [Gate(GateStatus.Passed)], Policy()).Kind);

    [Fact]
    public void New_dependency_triggers_takeover_only_when_review_enabled()
    {
        var state = State();
        state.AddedDependency = true;

        Assert.Equal(DecisionKind.Takeover, Sut.Decide(state, Ok, [], Policy()).Kind);
        Assert.Equal(DecisionKind.Accept, Sut.Decide(state, Ok, [], Policy(e => e.ReviewOnNewDependency = false)).Kind);
    }

    [Fact]
    public void Timeout_triggers_takeover() =>
        Assert.Equal(DecisionKind.Takeover, Sut.Decide(State(), Ok with { TimedOut = true }, [], Policy()).Kind);

    [Fact]
    public void No_changes_retries_then_escalates()
    {
        var empty = State();
        empty.ChangedPaths = [];
        Assert.Equal(DecisionKind.Retry, Sut.Decide(empty, Ok, [], Policy()).Kind);

        empty.Attempts = 2;
        Assert.Equal(DecisionKind.Takeover, Sut.Decide(empty, Ok, [], Policy()).Kind);
    }

    [Fact]
    public void Worker_exit_code_is_ignored_the_diff_is_judged() =>
        Assert.Equal(DecisionKind.Accept, Sut.Decide(State(), Ok with { ExitCode = 1 }, [Gate(GateStatus.Passed)], Policy()).Kind);
}
