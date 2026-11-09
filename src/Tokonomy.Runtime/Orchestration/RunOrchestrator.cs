using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Takeover;
using Tokonomy.Llm.Architect;
using Tokonomy.Llm.Providers;
using Tokonomy.Llm.Proxy;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Gates;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Process;
using Tokonomy.Runtime.Takeover;

namespace Tokonomy.Runtime.Orchestration;

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

    /// <summary>Model for takeovers. Defaults to <c>models.architect</c>.</summary>
    public string? ArchitectModel { get; init; }

    /// <summary>Gates in the loop (strategies 1b, 4). Without gates every finished attempt with changes is accepted.</summary>
    public bool UseGates { get; init; } = true;

    /// <summary>Architect escalation available (strategy 4): gate failures, no changes and timeouts trigger a takeover.</summary>
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
    void TakeoverFinished(WorkUnit unit, bool success, string note) { }
}

/// <summary>PLAN is done by the caller. This runs PREPARE → (EXECUTE → GATE → DECIDE)* per unit → LEDGER.</summary>
public sealed class RunOrchestrator(
    IProcessRunner runner,
    ILedger ledger,
    ISupervisor supervisor,
    Func<ExecutorSession, IExecutor>? executorFactory = null,
    IClock? clock = null,
    IProvider? architect = null)
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
            var locks = new List<LockedRegion>();
            var handoffs = new List<Handoff>();
            foreach (var unit in ContractValidator.Order(request.Contract))
            {
                var outcome = await RunUnitAsync(request, unit, workspace, executor, gates, baseline, completed, locks, handoffs, budget, observer, ct);
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
        Dictionary<string, GateRun> baseline, List<WorkUnit> completed, List<LockedRegion> locks, List<Handoff> handoffs,
        BudgetGuard budget, IRunObserver observer, CancellationToken ct)
    {
        var policy = request.Policy;
        var artifacts = request.Artifacts;
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
            var brief = BriefWriter.Build(request.Contract, unit, state.Attempts, request.Executor.BriefFile, feedback, completed, handoffs);
            artifacts.WriteLog($"{brief.Label}.brief.md", brief.Body);

            var run = await executor.RunAsync(brief, workspace.Path, request.WorkerModel,
                TimeSpan.FromSeconds(policy.Escalation.WorkerTimeoutSeconds), linked.Token);
            RecordReportedUsage(request.RunId, unit.Id, run, budget);

            var diff = await workspace.DiffAsync(unitStart, ct);
            state.ChangedPaths = diff.ChangedPaths;
            state.DiffLines = diff.TotalLines;
            state.AddedDependency = diff.AddedDependency;
            if (diff.Patch.Length > 0)
            {
                artifacts.WriteLog($"{brief.Label}.diff", diff.Patch);
            }

            var (gateResults, gateLogs) = await RunGatesAsync(request, brief.Label, workspace.Path, diff, gates, baseline, locks, linked.Token);

            var decision = supervisor.Decide(state, run, gateResults, policy);
            var takeover = decision.CanTakeOver && request.AllowEscalation && architect is not null &&
                           state.Takeovers < policy.Escalation.MaxTakeoversPerUnit;
            if (!takeover)
            {
                decision = Constrain(decision, request, state, maxAttempts, architect is not null);
            }

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

            artifacts.Append("attempt", new
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

            if (takeover)
            {
                var result = await TakeOverAsync(request, unit, workspace, unitStart, state, decision, diff, gateResults, gates, baseline, locks, handoffs, budget, observer, ct);
                observer.TakeoverFinished(unit, result.Success, result.Note);
                if (result.Success)
                {
                    ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "accepted", state.Attempts, state.Takeovers);
                    return new UnitOutcome(unit.Id, "accepted", state.Attempts, $"architect takeover: {result.Note}");
                }

                await workspace.ResetAsync(unitStart, CancellationToken.None);
                ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "failed", state.Attempts, state.Takeovers);
                return new UnitOutcome(unit.Id, "failed", state.Attempts, $"{decision.Reason}; takeover failed: {result.Note}");
            }

            switch (decision.Kind)
            {
                case DecisionKind.Accept:
                    await workspace.CommitAsync($"tokonomy({unit.Id}): {unit.Title}", ct);
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
    }

    /// <summary>Runs every command gate, then the locked-region check once an earlier takeover created locks.</summary>
    private static async Task<(List<GateResult> Results, List<string?> Logs)> RunGatesAsync(
        RunRequest request, string label, string workdir, UnitDiff diff, List<CommandGate> gates,
        Dictionary<string, GateRun> baseline, List<LockedRegion> locks, CancellationToken ct)
    {
        var results = new List<GateResult>();
        var logs = new List<string?>();
        if (diff.ChangedPaths.Count == 0)
        {
            return (results, logs);
        }

        foreach (var (gate, i) in gates.Select((g, i) => (g, i)))
        {
            var log = Path.Combine(request.Artifacts.LogsDirectory, $"{label}.gate{i}.log");
            var gateRun = await gate.ExecuteAsync(workdir, log, ct);
            results.Add(CommandGate.Evaluate(gateRun, baseline.GetValueOrDefault(gate.Name)));
            logs.Add(log);
        }

        if (request.UseGates && locks.Count > 0)
        {
            results.Add(LockedRegionGate.Evaluate(workdir, locks));
            logs.Add(null);
        }

        return (results, logs);
    }

    private sealed record TakeoverResult(bool Success, string Note);

    /// <summary>
    /// The architect fixes the failing unit with search/replace edits (the worker's changes stay in place), the full gate
    /// suite re-runs, and on success the edit is locked, a handoff is written and the unit is committed.
    /// </summary>
    private async Task<TakeoverResult> TakeOverAsync(
        RunRequest request, WorkUnit unit, RunWorkspace workspace, string unitStart, UnitState state, Decision decision,
        UnitDiff diff, List<GateResult> gateResults, List<CommandGate> gates, Dictionary<string, GateRun> baseline,
        List<LockedRegion> locks, List<Handoff> handoffs, BudgetGuard budget, IRunObserver observer, CancellationToken ct)
    {
        var policy = request.Policy;
        var artifacts = request.Artifacts;
        var model = request.ArchitectModel ?? policy.Models.Architect;
        state.Takeovers++;
        ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "takeover", state.Attempts, state.Takeovers);
        observer.Stage($"Takeover: the architect ({model}) steps in on {unit.Id} ({decision.Reason})");

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in diff.ChangedPaths.Concat(unit.Files).Distinct(StringComparer.Ordinal))
        {
            var full = Path.Combine(workspace.Path, path);
            if (File.Exists(full) && new FileInfo(full).Length <= 200_000)
            {
                files[path] = await File.ReadAllTextAsync(full, ct);
            }
        }

        var repoFiles = (await _git.RunAsync(workspace.Path, ["ls-files", "--cached", "--others", "--exclude-standard"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var failure = BriefWriter.Feedback(decision.Reason, gateResults);

        string? problem = null;
        TakeoverReply? reply = null;
        ApplyResult? applied = null;
        for (var ask = 1; ask <= 2; ask++)
        {
            var prompt = TakeoverPrompt.Build(request.Contract, unit, state.Attempts, decision.Reason, diff.Patch, failure, files, repoFiles, locks, problem);
            ProviderResponse response;
            try
            {
                response = await architect!.CompleteAsync(new ProviderRequest(model, TakeoverPrompt.System, prompt), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.ClientModel.ClientResultException)
            {
                return new TakeoverResult(false, $"the architect call failed: {ex.Message}");
            }

            RecordArchitectCall(request.RunId, unit.Id, response, budget);
            artifacts.WriteLog($"{unit.Id}-takeover{ask}.reply.md", response.Text);
            if (budget.Exhausted)
            {
                return new TakeoverResult(false, "budget exhausted");
            }

            reply = TakeoverPrompt.Parse(response.Text);
            applied = EditApplier.Apply(workspace.Path, reply.Edits);
            if (applied.Ok)
            {
                break;
            }

            problem = string.Join("\n", applied.Errors);
        }

        if (applied is not { Ok: true } || reply is null)
        {
            return new TakeoverResult(false, $"the architect's edits could not be applied: {problem}");
        }

        var newDiff = await workspace.DiffAsync(unitStart, ct);
        if (newDiff.Patch.Length > 0)
        {
            artifacts.WriteLog($"{unit.Id}-takeover.diff", newDiff.Patch);
        }

        var (after, afterLogs) = await RunGatesAsync(request, $"{unit.Id}-takeover", workspace.Path, newDiff, gates, baseline, locks, ct);
        var blocking = after.Where(g => g.Blocking).Select(g => g.Gate).ToList();
        var attemptId = ledger.RecordAttempt(new AttemptRecord
        {
            RunId = request.RunId,
            UnitId = unit.Id,
            Number = state.Attempts + 1,
            Executor = "architect",
            Model = model,
            ExitCode = 0,
            Duration = TimeSpan.Zero,
            DiffLines = newDiff.TotalLines,
            Decision = blocking.Count == 0 ? DecisionKind.Accept : DecisionKind.Fail,
            Reason = blocking.Count == 0 ? "takeover: gates pass" : $"takeover: gate(s) still failing: {string.Join(", ", blocking)}",
        });
        for (var i = 0; i < after.Count; i++)
        {
            ledger.RecordGateResult(attemptId, after[i], afterLogs[i]);
        }

        artifacts.Append("takeover", new
        {
            unit = unit.Id,
            trigger = decision.Trigger,
            files = applied.Files,
            gates = after.Select(g => new { g.Gate, g.Status }),
            ok = blocking.Count == 0,
        });
        if (blocking.Count > 0)
        {
            return new TakeoverResult(false, $"gate(s) still failing after the architect's edit: {string.Join(", ", blocking)}");
        }

        var newLocks = reply.Edits
            .Where(e => !string.IsNullOrWhiteSpace(e.Replace))
            .Select(e => new LockedRegion(e.Path.Replace('\\', '/').TrimStart('/'), e.Replace))
            .ToList();
        locks.AddRange(newLocks);

        var handoff = new Handoff
        {
            UnitId = unit.Id,
            UnitTitle = unit.Title,
            Files = applied.Files,
            Summary = reply.Summary ?? "the architect corrected this unit",
            Why = reply.Why ?? decision.Reason,
            Locked = newLocks,
            MustFollow = request.Contract.Rules.Select(r => $"{r.Id}: {r.Text}").ToList(),
        };
        handoffs.Add(handoff);
        var order = ContractValidator.Order(request.Contract).ToList();
        var next = order.SkipWhile(u => u.Id != unit.Id).Skip(1).FirstOrDefault();
        var yaml = HandoffWriter.ToYaml(handoff, next is null ? null : $"{next.Id}: {next.Title}");
        artifacts.WriteFile("handoff.yaml", yaml);
        artifacts.WriteFile($"handoff-{unit.Id}.yaml", yaml);

        await workspace.CommitAsync($"tokonomy({unit.Id}): {unit.Title} [architect takeover]", ct);
        return new TakeoverResult(true, handoff.Summary);
    }

    /// <summary>Records usage the executor reported itself (not metered by the proxy); always marked estimated.</summary>
    private void RecordReportedUsage(string runId, string unitId, RunResult run, BudgetGuard budget)
    {
        foreach (var usage in run.Usage)
        {
            ledger.RecordLlmCall(new LlmCallRecord
            {
                RunId = runId,
                UnitId = unitId,
                Role = LlmRole.Worker,
                Model = usage.Model,
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                CachedTokens = usage.CachedTokens,
                Usd = usage.Usd ?? 0,
                Source = CostSource.Estimated,
            });
            budget.Add(usage.Usd ?? 0);
        }
    }

    private void RecordArchitectCall(string runId, string unitId, ProviderResponse response, BudgetGuard budget)
    {
        ledger.RecordLlmCall(new LlmCallRecord
        {
            RunId = runId,
            UnitId = unitId,
            Role = LlmRole.Architect,
            Model = response.Model,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            CachedTokens = response.CachedTokens,
            Usd = response.Usd ?? 0,
            Source = response.Usd is null || response.Estimated ? CostSource.Estimated : CostSource.Metered,
        });
        budget.Add(response.Usd ?? 0);
    }

    /// <summary>Applies strategy limits on top of the supervisor's policy decision, for escalations that are not taken.</summary>
    internal static Decision Constrain(Decision decision, RunRequest request, UnitState state, int maxAttempts, bool architectAvailable)
    {
        if (decision.Kind is DecisionKind.Takeover or DecisionKind.Split)
        {
            if (!request.AllowEscalation)
            {
                return new Decision(DecisionKind.Fail, $"{decision.Reason}; no architect escalation in this strategy");
            }

            if (decision.Kind == DecisionKind.Split)
            {
                return new Decision(DecisionKind.Fail, $"{decision.Reason}; splitting an oversized unit is not implemented yet");
            }

            if (!decision.CanTakeOver)
            {
                return new Decision(DecisionKind.Fail, $"{decision.Reason}; this needs a human decision, not an architect edit");
            }

            var why = !architectAvailable ? "no architect provider" : "takeover limit reached for this unit";
            return new Decision(DecisionKind.Fail, $"{decision.Reason}; takeover unavailable ({why})");
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
