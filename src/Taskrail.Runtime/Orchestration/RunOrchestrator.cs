using Taskrail.Core.Abstractions;
using Taskrail.Core.Config;
using Taskrail.Core.Model;
using Taskrail.Core.Planning;
using Taskrail.Llm.Providers;
using Taskrail.Llm.Proxy;
using Taskrail.Runtime.Executors;
using Taskrail.Runtime.Gates;
using Taskrail.Runtime.Git;
using Taskrail.Runtime.Ledger;
using Taskrail.Runtime.Process;

namespace Taskrail.Runtime.Orchestration;

public sealed record RunRequest
{
    public required string RunId { get; init; }
    public required Contract Contract { get; init; }
    public required string RepoRoot { get; init; }
    public required string BaseSha { get; init; }
    public required Policy Policy { get; init; }
    public required string ExecutorName { get; init; }
    public required ExecutorDefinition Executor { get; init; }
    public required RunArtifacts Artifacts { get; init; }
    public string? WorkerModel { get; init; }

    /// <summary>Gates in the loop (strategies 1b, 4). Without gates every finished attempt with changes is accepted.</summary>
    public bool UseGates { get; init; } = true;

    /// <summary>Architect escalation available (takeover lands in M2; until then Takeover/Split fail the unit).</summary>
    public bool AllowEscalation { get; init; } = true;

    /// <summary>Cap on attempts per unit regardless of policy (1 for single-shot strategies).</summary>
    public int? MaxAttempts { get; init; }

    public PriceTable Prices { get; init; } = PriceTable.Empty;

    /// <summary>Provider key for the metering proxy; required when the executor is proxy-routable.</summary>
    public string? UpstreamKey { get; init; }

    /// <summary>Cost already spent on this task (architect planning) that counts against the budget.</summary>
    public decimal AlreadySpentUsd { get; init; }

    public string? WorktreeLocation { get; init; }
}

public sealed record UnitOutcome(string UnitId, string Status, int Attempts, string Reason);

public sealed record RunOutcome
{
    public required RunStatus Status { get; init; }
    public required string Branch { get; init; }
    public string? WorktreePath { get; init; }
    public required string HeadSha { get; init; }
    public required decimal TotalUsd { get; init; }
    public required IReadOnlyList<UnitOutcome> Units { get; init; }
    public string? Error { get; init; }
}

/// <summary>Progress callbacks for the CLI. All methods are optional.</summary>
public interface IRunObserver
{
    void Stage(string message) { }
    void UnitStarted(WorkUnit unit) { }
    void AttemptFinished(WorkUnit unit, int attempt, RunResult run, UnitDiff diff, IReadOnlyList<GateResult> gates, Decision decision) { }
    void Warning(string message) { }
}

/// <summary>PLAN is done by the caller. This runs PREPARE → (EXECUTE → GATE → DECIDE)* per unit → LEDGER.</summary>
public sealed class RunOrchestrator(
    IProcessRunner runner,
    ILedger ledger,
    ISupervisor supervisor,
    Func<ExecutorSession, IExecutor>? executorFactory = null,
    IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly GitCli _git = new(runner);

    // Unit currently executing, so metered worker calls (reported on proxy threads) are attributed to it.
    private volatile string? _currentUnit;

    public async Task<RunOutcome> RunAsync(RunRequest request, IRunObserver? observer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        observer ??= new NullObserver();
        var policy = request.Policy;
        var artifacts = request.Artifacts;
        var units = new List<UnitOutcome>();
        var status = RunStatus.Running;
        string? error = null;

        ledger.SetRunStatus(request.RunId, RunStatus.Running);
        using var budget = new BudgetGuard(policy.Budget.MaxUsdPerTask, request.AlreadySpentUsd);

        observer.Stage("Creating worktree");
        var workspace = await RunWorkspace.CreateAsync(_git, request.RepoRoot, request.RunId, request.BaseSha, ct, request.WorktreeLocation);
        artifacts.Append("worktree", new { workspace.Path, workspace.Branch, workspace.BaseSha });

        MeteringProxy? proxy = null;
        try
        {
            proxy = await StartProxyAsync(request, budget, observer);
            var session = new ExecutorSession
            {
                ProxyUrl = proxy?.Url ?? "",
                ProxyRoot = proxy?.Root ?? "",
                ProxyToken = proxy?.Token ?? "",
                LogDirectory = artifacts.LogsDirectory,
                ExtraAllowedEnv = policy.Isolation.WorkerEnvAllow,
            };
            var executor = executorFactory?.Invoke(session) ?? new ConfigExecutor(request.ExecutorName, request.Executor, session, runner);

            var gateEnv = EnvironmentBuilder.ForGate([policy.Provider.ApiKeyEnv]);
            if (!await PrepareAsync(policy, workspace.Path, gateEnv, artifacts, observer, ct))
            {
                status = RunStatus.Error;
                error = "prepare step failed";
                return Finish();
            }

            var gates = request.UseGates
                ? policy.Gates.Where(g => g.Type == GateConfig.CommandType).Select(g => new CommandGate(g, runner, gateEnv)).ToList()
                : [];
            var baseline = new Dictionary<string, GateRun>(StringComparer.Ordinal);
            if (gates.Count > 0)
            {
                observer.Stage($"Running {gates.Count} gate(s) on the base commit (baseline)");
                foreach (var (gate, i) in gates.Select((g, i) => (g, i)))
                {
                    var run = await gate.ExecuteAsync(workspace.Path, Path.Combine(artifacts.LogsDirectory, $"baseline-gate{i}.log"), ct);
                    baseline[gate.Name] = run;
                    artifacts.Append("baseline", new { gate = gate.Name, run.Passed, failing = run.FailingTests?.Count });
                    if (!run.Passed)
                    {
                        observer.Warning($"Gate '{gate.Name}' already fails on the base commit; only new failures will block.");
                    }
                }
            }

            var completed = new List<WorkUnit>();
            foreach (var unit in ContractValidator.Order(request.Contract))
            {
                var outcome = await RunUnitAsync(request, unit, workspace, executor, gates, baseline, completed, budget, observer, ct);
                units.Add(outcome);
                if (outcome.Status != "accepted")
                {
                    status = budget.Exhausted ? RunStatus.AbortedBudget : RunStatus.Fail;
                    error = $"{unit.Id}: {outcome.Reason}";
                    return Finish();
                }

                completed.Add(unit);
            }

            status = RunStatus.Pass;
            return Finish();
        }
        catch (OperationCanceledException) when (budget.Exhausted && !ct.IsCancellationRequested)
        {
            status = RunStatus.AbortedBudget;
            error = $"budget of ${policy.Budget.MaxUsdPerTask} exhausted";
            return Finish();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = RunStatus.Error;
            error = ex.Message;
            artifacts.Append("error", new { ex.Message, type = ex.GetType().Name });
            return Finish();
        }
        finally
        {
            if (proxy is not null)
            {
                await proxy.DisposeAsync();
            }

            if (!policy.Isolation.KeepWorktree)
            {
                await workspace.RemoveAsync(CancellationToken.None);
            }

            if (ct.IsCancellationRequested)
            {
                ledger.FinishRun(request.RunId, RunStatus.Error, _clock.UtcNow);
            }
        }

        RunOutcome Finish()
        {
            ledger.FinishRun(request.RunId, status, _clock.UtcNow);
            var head = _git.HeadAsync(workspace.Path, CancellationToken.None).GetAwaiter().GetResult();
            artifacts.Append("finished", new { status, error, head });
            return new RunOutcome
            {
                Status = status,
                Branch = workspace.Branch,
                WorktreePath = policy.Isolation.KeepWorktree ? workspace.Path : null,
                HeadSha = head,
                TotalUsd = budget.Spent,
                Units = units,
                Error = error,
            };
        }
    }

    private async Task<UnitOutcome> RunUnitAsync(
        RunRequest request, WorkUnit unit, RunWorkspace workspace, IExecutor executor, List<CommandGate> gates,
        Dictionary<string, GateRun> baseline, List<WorkUnit> completed, BudgetGuard budget, IRunObserver observer, CancellationToken ct)
    {
        var policy = request.Policy;
        var state = new UnitState { Unit = unit };
        var unitStart = await workspace.HeadAsync(ct);
        var maxAttempts = request.MaxAttempts ?? int.MaxValue;
        string? feedback = null;
        observer.UnitStarted(unit);
        ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "running", 0, 0);
        _currentUnit = unit.Id;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        while (true)
        {
            state.Attempts++;
            var brief = BriefWriter.Build(request.Contract, unit, state.Attempts, request.Executor.BriefFile, feedback, completed);
            artifacts(request).WriteLog($"{brief.Label}.brief.md", brief.Body);

            var run = await executor.RunAsync(brief, workspace.Path, request.WorkerModel,
                TimeSpan.FromSeconds(policy.Escalation.WorkerTimeoutSeconds), linked.Token);

            var diff = await workspace.DiffAsync(unitStart, ct);
            state.ChangedPaths = diff.ChangedPaths;
            state.DiffLines = diff.TotalLines;
            state.AddedDependency = diff.AddedDependency;
            if (diff.Patch.Length > 0)
            {
                artifacts(request).WriteLog($"{brief.Label}.diff", diff.Patch);
            }

            var gateResults = new List<GateResult>();
            var gateLogs = new List<string>();
            if (diff.ChangedPaths.Count > 0)
            {
                foreach (var (gate, i) in gates.Select((g, i) => (g, i)))
                {
                    var log = Path.Combine(artifacts(request).LogsDirectory, $"{brief.Label}.gate{i}.log");
                    var gateRun = await gate.ExecuteAsync(workspace.Path, log, linked.Token);
                    gateResults.Add(CommandGate.Evaluate(gateRun, baseline.GetValueOrDefault(gate.Name)));
                    gateLogs.Add(log);
                }
            }

            var decision = supervisor.Decide(state, run, gateResults, policy);
            decision = Constrain(decision, request, state, maxAttempts);
            observer.AttemptFinished(unit, state.Attempts, run, diff, gateResults, decision);

            var attemptId = ledger.RecordAttempt(new AttemptRecord
            {
                RunId = request.RunId,
                UnitId = unit.Id,
                Number = state.Attempts,
                Executor = executor.Name,
                Model = request.WorkerModel,
                ExitCode = run.ExitCode,
                Duration = run.Duration,
                DiffLines = diff.TotalLines,
                Decision = decision.Kind,
                Reason = decision.Reason,
            });
            for (var i = 0; i < gateResults.Count; i++)
            {
                ledger.RecordGateResult(attemptId, gateResults[i], gateLogs[i]);
            }

            artifacts(request).Append("attempt", new
            {
                unit = unit.Id,
                attempt = state.Attempts,
                run.ExitCode,
                run.TimedOut,
                diff_lines = diff.TotalLines,
                changed = diff.ChangedPaths,
                gates = gateResults.Select(g => new { g.Gate, g.Status, g.NewFailures }),
                decision = decision.Kind,
                decision.Reason,
            });

            switch (decision.Kind)
            {
                case DecisionKind.Accept:
                    await workspace.CommitAsync($"taskrail({unit.Id}): {unit.Title}", ct);
                    ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "accepted", state.Attempts, state.Takeovers);
                    return new UnitOutcome(unit.Id, "accepted", state.Attempts, decision.Reason);

                case DecisionKind.Retry:
                    feedback = BriefWriter.Feedback(decision.Reason, gateResults);
                    continue;

                default:
                    await workspace.ResetAsync(unitStart, CancellationToken.None);
                    ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "failed", state.Attempts, state.Takeovers);
                    return new UnitOutcome(unit.Id, "failed", state.Attempts, decision.Reason);
            }
        }

        static RunArtifacts artifacts(RunRequest r) => r.Artifacts;
    }

    /// <summary>Applies strategy limits on top of the supervisor's policy decision.</summary>
    internal static Decision Constrain(Decision decision, RunRequest request, UnitState state, int maxAttempts)
    {
        if (decision.Kind is DecisionKind.Takeover or DecisionKind.Split)
        {
            return request.AllowEscalation
                ? new Decision(DecisionKind.Fail, $"{decision.Reason}; {decision.Kind.ToString().ToLowerInvariant()} lands in M2")
                : new Decision(DecisionKind.Fail, $"{decision.Reason}; no architect escalation in this strategy");
        }

        if (decision.Kind == DecisionKind.Retry && state.Attempts >= maxAttempts)
        {
            return new Decision(DecisionKind.Fail, $"{decision.Reason}; attempt limit {maxAttempts} reached");
        }

        return decision;
    }

    private async Task<bool> PrepareAsync(Policy policy, string workdir, Dictionary<string, string> env, RunArtifacts artifacts, IRunObserver observer, CancellationToken ct)
    {
        foreach (var (step, i) in policy.Prepare.Select((s, i) => (s, i)))
        {
            observer.Stage($"Prepare: {step.DisplayName}");
            var outcome = await runner.RunAsync(new ProcessSpec
            {
                Args = step.Cmd,
                WorkingDirectory = workdir,
                Environment = env,
                ReplaceEnvironment = true,
                Timeout = TimeSpan.FromSeconds(step.TimeoutSeconds),
                StdoutFile = Path.Combine(artifacts.LogsDirectory, $"prepare{i}.log"),
            }, ct);
            artifacts.Append("prepare", new { step = step.DisplayName, outcome.ExitCode, outcome.TimedOut });
            if (!outcome.Succeeded)
            {
                observer.Warning($"Prepare step '{step.DisplayName}' failed (exit {outcome.ExitCode}): {CommandGate.Tail(outcome.Stderr + outcome.Stdout, 800)}");
                return false;
            }
        }

        return true;
    }

    private async Task<MeteringProxy?> StartProxyAsync(RunRequest request, BudgetGuard budget, IRunObserver observer)
    {
        var caps = request.Executor.Capabilities;
        if (executorFactory is not null || !caps.ProxyRoutable)
        {
            if (!caps.ReportsTokens)
            {
                ledger.MarkEstimated(request.RunId);
                observer.Warning($"Executor '{request.ExecutorName}' is not metered; worker cost is recorded as estimated.");
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(request.UpstreamKey))
        {
            throw new InvalidOperationException($"Provider key ({request.Policy.Provider.ApiKeyEnv}) is required to meter worker traffic.");
        }

        var runId = request.RunId;
        return await MeteringProxy.StartAsync(
            new ProxyOptions { Upstream = new Uri(request.Policy.Provider.BaseUrl), UpstreamKey = request.UpstreamKey },
            request.Prices,
            usage =>
            {
                var estimated = !usage.Reported || usage.Usd is null;
                ledger.RecordLlmCall(new LlmCallRecord
                {
                    RunId = runId,
                    UnitId = _currentUnit,
                    Role = LlmRole.Worker,
                    Model = usage.Model,
                    InputTokens = usage.InputTokens,
                    OutputTokens = usage.OutputTokens,
                    CachedTokens = usage.CachedTokens,
                    Usd = usage.Usd ?? 0,
                    Source = estimated ? CostSource.Estimated : CostSource.Metered,
                });
                budget.Add(usage.Usd ?? 0);
            },
            () => budget.Exhausted);
    }

    private sealed class NullObserver : IRunObserver;
}
