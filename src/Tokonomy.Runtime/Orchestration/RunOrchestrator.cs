using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Supervision;
using Tokonomy.Core.Takeover;
using Tokonomy.Llm.Architect;
using Tokonomy.Llm.Providers;
using Tokonomy.Llm.Proxy;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Gates;
using Tokonomy.Runtime.Gates.Isolation;
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

    /// <summary>
    /// Strategy 5: the architect supervises (worker reports, L1-L3 interventions, plan revisions, lessons, decomposition,
    /// recovery) and reviews units by risk. Requires <see cref="AllowEscalation"/> and gates.
    /// </summary>
    public bool AdaptiveSupervision { get; init; }

    /// <summary>Cap on attempts per unit regardless of policy (1 for single-shot strategies).</summary>
    public int? MaxAttempts { get; init; }

    public PriceTable Prices { get; init; } = PriceTable.Empty;

    /// <summary>Provider key for the metering proxy; required when the executor is proxy-routable.</summary>
    public string? UpstreamKey { get; init; }

    /// <summary>Cost already spent on this task (architect planning) that counts against the budget.</summary>
    public decimal AlreadySpentUsd { get; init; }

    public string? WorktreeLocation { get; init; }

    /// <summary>Set by <c>run --resume</c>: continue this interrupted run on its existing branch instead of starting it.</summary>
    public ResumeRequest? Resume { get; init; }
}

/// <summary>
/// How one unit ended. <see cref="Status"/>: accepted, failed, or split (replaced by smaller units in the plan, which
/// appear as their own outcomes).
/// </summary>
public sealed record UnitOutcome(string UnitId, string Status, int Attempts, string Reason)
{
    public const string Accepted = "accepted";
    public const string Failed = "failed";
    public const string Split = "split";

    public int Interventions { get; init; }

    /// <summary>The architectural review's verdict, when the unit was reviewed.</summary>
    public UnitReviewVerdict? Review { get; init; }
}

public sealed record RunOutcome
{
    public required RunStatus Status { get; init; }
    public required string Branch { get; init; }
    public string? WorktreePath { get; init; }
    public required string HeadSha { get; init; }
    public required decimal TotalUsd { get; init; }
    public required IReadOnlyList<UnitOutcome> Units { get; init; }
    public string? Error { get; init; }

    /// <summary>The architect's advisory review of the combined diff (only when <c>escalation.final_review</c> is on and the run passed).</summary>
    public FinalReview? Review { get; init; }

    /// <summary>The plan revision the run ended on (1 when the plan never changed).</summary>
    public int PlanRevision { get; init; } = 1;

    public int Interventions { get; init; }
    public int Lessons { get; init; }
}

/// <summary>Progress callbacks for the CLI. All methods are optional.</summary>
public interface IRunObserver
{
    void Stage(string message) { }
    void UnitStarted(WorkUnit unit) { }
    void AttemptStarted(WorkUnit unit, int attempt) { }
    void GatesStarted(WorkUnit unit) { }
    void AttemptFinished(WorkUnit unit, int attempt, RunResult run, UnitDiff diff, IReadOnlyList<GateResult> gates, Decision decision) { }
    void Warning(string message) { }
    void TakeoverFinished(WorkUnit unit, bool success, string note) { }

    /// <summary>One model call was billed. <paramref name="totalUsd"/> is the run's spend so far (including earlier planning cost).</summary>
    void Usage(LlmRole role, string model, long inputTokens, long outputTokens, decimal usd, decimal totalUsd) { }

    /// <summary>The worker wrote a structured report (adaptive supervision).</summary>
    void WorkerReported(WorkUnit unit, WorkerReport report) { }

    /// <summary>The architect is called to supervise a unit.</summary>
    void SupervisionStarted(WorkUnit unit, SupervisionRequest request) { }

    /// <summary>An intervention finished: what the architect found, what it changed, and how it ended.</summary>
    void InterventionFinished(WorkUnit unit, InterventionRecord intervention) { }

    /// <summary>The plan changed during the run.</summary>
    void PlanUpdated(PlanRevision revision) { }

    /// <summary>The architect reviewed a unit whose gates passed.</summary>
    void ReviewFinished(WorkUnit unit, UnitReview review, string why) { }
}

/// <summary>
/// PLAN is done by the caller. This runs PREPARE → (EXECUTE → GATE → DECIDE)* per unit → LEDGER. The units come from a
/// <see cref="PlanManager"/>: with adaptive supervision (strategy 5) the plan can change while the run executes it.
/// </summary>
public sealed partial class RunOrchestrator(
    IProcessRunner runner,
    ILedger ledger,
    ISupervisor supervisor,
    Func<ExecutorSession, IExecutor>? executorFactory = null,
    IClock? clock = null,
    IProvider? architect = null,
    IGateExecutionProvider? gateProvider = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly GitCli _git = new(runner);

    // A provider timeout must mean "architect unavailable" (and its fallback), never an exception that ends the run.
    private readonly IProvider? architect = Tokonomy.Runtime.Providers.GuardedProvider.Wrap(architect);

    // Unit currently executing, so metered worker calls (reported on proxy threads) are attributed to it.
    private volatile string? _currentUnit;
    private volatile IRunObserver? _usageObserver;

    public async Task<RunOutcome> RunAsync(RunRequest request, IRunObserver? observer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var work = Tokonomy.Runtime.Process.ChildProcesses.BeginWork(); // a Ctrl+C waits for this run's cleanup
        observer ??= new NullObserver();
        _usageObserver = observer;
        var policy = request.Policy;
        var artifacts = request.Artifacts;
        var units = new List<UnitOutcome>();
        var status = RunStatus.Running;
        string? error = null;
        FinalReview? review = null;
        RunContext? context = null;

        // One process per run: a live run cannot be resumed (or started) twice. The OS releases the lock if we crash.
        IDisposable runLock;
        try
        {
            runLock = artifacts.AcquireLock();
        }
        catch (RunInProgressException ex)
        {
            return new RunOutcome { Status = RunStatus.Error, Branch = "", HeadSha = request.BaseSha, TotalUsd = request.AlreadySpentUsd, Units = [], Error = ex.Message };
        }

        using var heldLock = runLock;
        ledger.SetRunStatus(request.RunId, RunStatus.Running);
        using var budget = new BudgetGuard(policy.Budget.MaxUsdPerTask, request.AlreadySpentUsd);

        // Where gates run is settled before anything else exists: an unusable provider stops the run here, before a
        // worktree or a worker, and nothing ever falls back to running gates on the host.
        IGateExecutionProvider? provider;
        try
        {
            provider = await ResolveGateProviderAsync(request, observer, ct);
        }
        catch (GateIsolationUnavailableException ex)
        {
            artifacts.Append("error", new { ex.Message, type = ex.GetType().Name });
            ledger.FinishRun(request.RunId, RunStatus.Error, _clock.UtcNow);
            return new RunOutcome
            {
                Status = RunStatus.Error,
                Branch = "",
                HeadSha = request.BaseSha,
                TotalUsd = budget.Spent,
                Units = [],
                Error = $"gate isolation unavailable: {ex.Message}",
            };
        }

        RunWorkspace workspace;
        if (request.Resume is null)
        {
            observer.Stage("Creating worktree");
            workspace = await RunWorkspace.CreateAsync(_git, request.RepoRoot, request.RunId, request.BaseSha, ct, request.WorktreeLocation, policy.Isolation.WorktreeRoot);
        }
        else
        {
            observer.Stage("Resuming: attaching a fresh worktree to the run branch");
            string? salvaged;
            (workspace, salvaged) = await RunWorkspace.ReattachAsync(_git, request.RepoRoot, request.RunId, request.BaseSha,
                Path.Combine(artifacts.Directory, "recovery"), ct, request.WorktreeLocation, policy.Isolation.WorktreeRoot);
            if (salvaged is not null)
            {
                artifacts.Append("recovery_salvage", new { patch = salvaged });
                observer.Warning($"The interrupted run left unverified changes; they were saved to {salvaged} and are not reused.");
            }
        }

        artifacts.Append("worktree", new { workspace.Path, workspace.Branch, workspace.BaseSha, resumed = request.Resume is not null });

        MeteringProxy? proxy = null;
        var watch = new WorkspaceWatch(workspace);
        string? dependencies = null;
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
                GateCommands = policy.Gates
                    .Where(g => g.Type == GateConfig.CommandType && g.Cmd.Count > 0)
                    .Select(g => string.Join(' ', g.Cmd))
                    .ToList(),
                Restrictions = watch.Restrictions,
            };
            var executor = executorFactory?.Invoke(session) ?? new ConfigExecutor(request.ExecutorName, request.Executor, session, runner);

            RecoveryDecision? recovery = null;
            if (request.Resume is { } resume)
            {
                recovery = RunRecovery.Reconcile(request.RunId, request.Contract, resume, await workspace.CommitsSinceAsync(request.BaseSha, ct),
                    ledger.ListAttempts(request.RunId), ledger.ListInterventions(request.RunId), ledger.ListPlanRevisions(request.RunId));
                artifacts.Append("recovery", new { safe = recovery.Safe, recovery.Problems, recovery.Notes });
                foreach (var note in recovery.Notes)
                {
                    observer.Stage($"Recovery: {note}");
                }

                if (!recovery.Safe)
                {
                    status = RunStatus.Error;
                    error = "the run cannot be resumed safely: " + string.Join("; ", recovery.Problems);
                    return Finish();
                }

                foreach (var revision in recovery.RevisionsToRecord)
                {
                    ledger.RecordPlanRevision(request.RunId, revision);
                }

                // Prepare and the baseline belong to the base commit, exactly as in the original run.
                await workspace.CheckoutDetachedAsync(request.BaseSha, ct);
            }

            if (policy.Prepare.Count > 0)
            {
                var prepared = await PrepareAsync(request, workspace, provider!, observer, ct);
                if (!prepared.Ok)
                {
                    status = RunStatus.Error;
                    error = "prepare step failed";
                    return Finish();
                }

                dependencies = prepared.Dependencies;
            }

            var commandGates = request.UseGates
                ? policy.Gates.Where(g => g.Type == GateConfig.CommandType).Select(g => new CommandGate(g)).ToList()
                : [];
            AcceptanceFiles? acceptance = request.UseGates && policy.Acceptance.Enabled ? new AcceptanceFiles(policy.Acceptance.Dir) : null;
            var acceptanceGate = acceptance is null
                ? null
                : new CommandGate(new GateConfig { Name = AcceptanceFiles.GateName, Cmd = policy.Acceptance.Cmd, TimeoutSeconds = policy.Acceptance.TimeoutSeconds });
            var gates = new GateSession(provider, request, workspace, dependencies, commandGates, observer, acceptance, acceptanceGate);
            var baseline = new Dictionary<string, GateRun>(StringComparer.Ordinal);
            if (commandGates.Count > 0)
            {
                observer.Stage($"Running {commandGates.Count} gate(s) on the base commit (baseline)");
                foreach (var (gate, run, _) in await gates.RunAllAsync("baseline", i => $"baseline-gate{i}.log", ct, includeAcceptance: false))
                {
                    baseline[gate.Name] = run;
                    artifacts.Append("baseline", new { gate = gate.Name, run.Passed, failing = run.FailingTests?.Count });
                    if (!run.Passed)
                    {
                        observer.Warning($"Gate '{gate.Name}' already fails on the base commit; only new failures will block.");
                    }
                }
            }

            if (recovery is not null)
            {
                await workspace.ReturnToBranchAsync(ct);
            }

            // Links present now came from the repository, from prepare (e.g. a pnpm install) or from the base commit's own
            // gates. They are allowed to stay,
            // but the worker is denied access to the ones that lead outside; any link that appears later is a violation.
            var escaping = watch.TakeBaseline();
            if (escaping.Count > 0)
            {
                artifacts.Append("links", new { escaping = escaping.Select(l => new { l.RelativePath, l.Target }) });
                observer.Warning($"{escaping.Count} link(s) in the worktree lead outside it ({string.Join(", ", escaping.Take(3).Select(l => l.RelativePath))}); the worker is denied access to them.");
            }

            // Plan updates may never target the policy, a protected path or the architect's acceptance tests.
            var acceptanceDir = acceptance?.Dir;
            List<string> planProtected = [.. policy.Escalation.ProtectedPaths, .. acceptanceDir is null ? Array.Empty<string>() : [acceptanceDir + "/**"]];
            var plan = recovery?.Plan is { } restored
                ? PlanManager.Restore(restored, planProtected, policy.Supervision.MaxPlanRevisions, _clock)
                : new PlanManager(request.Contract, planProtected, policy.Supervision.MaxPlanRevisions, _clock);
            context = new RunContext
            {
                Request = request,
                Workspace = workspace,
                Watch = watch,
                Executor = executor,
                Gates = gates,
                Baseline = baseline,
                Plan = plan,
                Budget = budget,
                Observer = observer,
                AcceptanceDir = acceptanceDir,
            };
            if (recovery is not null && !await RestoreAsync(context, recovery, ct))
            {
                status = RunStatus.Error;
                error = context.RecoveryError;
                return Finish();
            }

            SavePlanState(context);
            SaveRunState(context, RunPhases.Idle);

            while (plan.NextPending() is { } next)
            {
                plan.MarkRunning(next.Id);
                var outcome = await RunUnitAsync(context, next.Id, ct);
                units.Add(outcome);
                switch (outcome.Status)
                {
                    case UnitOutcome.Accepted:
                        plan.MarkAccepted(next.Id);
                        context.Completed.Add(plan.Unit(next.Id));
                        SavePlanState(context);
                        SaveRunState(context, RunPhases.Idle);
                        continue;
                    case UnitOutcome.Split:
                        // The plan now holds the smaller units in its place; the loop picks them up.
                        SavePlanState(context);
                        SaveRunState(context, RunPhases.Idle);
                        continue;
                }

                plan.MarkFailed(next.Id);
                SavePlanState(context);
                SaveRunState(context, RunPhases.Finished);
                status = budget.Exhausted ? RunStatus.AbortedBudget : RunStatus.Fail;
                error = $"{next.Id}: {outcome.Reason}";
                return Finish();
            }

            if (plan.Remaining().Count > 0)
            {
                status = RunStatus.Error;
                error = $"units could not be scheduled: {string.Join(", ", plan.Remaining().Select(u => u.Id))}";
                return Finish();
            }

            status = RunStatus.Pass;
            SaveRunState(context, RunPhases.Finished);
            review = await FinalReviewAsync(request, plan.Current, workspace, units, budget, observer, ct);
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

            if (dependencies is not null && WorktreeGuard.DeleteTree(Path.GetDirectoryName(dependencies)!) is { } depsLeft)
            {
                observer.Warning(depsLeft);
            }

            if (!policy.Isolation.KeepWorktree && await workspace.RemoveAsync(CancellationToken.None) is { } leftBehind)
            {
                artifacts.Append("cleanup", new { warning = leftBehind });
                observer.Warning(leftBehind);
            }

            if (ct.IsCancellationRequested)
            {
                ledger.FinishRun(request.RunId, RunStatus.Error, _clock.UtcNow);
                if (context is not null)
                {
                    SavePlanState(context);
                }
            }
        }

        RunOutcome Finish()
        {
            ledger.FinishRun(request.RunId, status, _clock.UtcNow);
            var head = workspace.HeadAsync(CancellationToken.None).GetAwaiter().GetResult();
            artifacts.Append("finished", new { status, error, head, plan_revision = context?.Plan.Revision });
            return new RunOutcome
            {
                Status = status,
                Branch = workspace.Branch,
                WorktreePath = policy.Isolation.KeepWorktree ? workspace.Path : null,
                HeadSha = head,
                TotalUsd = budget.Spent,
                Units = units,
                Error = error,
                Review = review,
                PlanRevision = context?.Plan.Revision ?? 1,
                Interventions = context?.RunInterventions ?? 0,
                Lessons = context?.Plan.Lessons.Count ?? 0,
            };
        }
    }

    /// <summary>
    /// One architect call over the combined diff once every unit passed. Advisory: a failed or unreadable review is
    /// recorded as unavailable and never fails the run.
    /// </summary>
    private async Task<FinalReview?> FinalReviewAsync(
        RunRequest request, Contract plan, RunWorkspace workspace, List<UnitOutcome> units, BudgetGuard budget, IRunObserver observer, CancellationToken ct)
    {
        var policy = request.Policy;
        if (!policy.Escalation.FinalReview || !request.AllowEscalation || architect is null)
        {
            return null;
        }

        var model = request.ArchitectModel ?? policy.Models.Architect;
        observer.Stage($"Final review: the architect ({model}) reads the combined diff");
        var diff = await workspace.DiffAsync(request.BaseSha, ct);
        var reviewed = units.Where(u => u.Status != UnitOutcome.Split).ToList();
        var prompt = ReviewPrompt.Build(plan, diff.Patch, reviewed.Select(u => (u.UnitId, plan.Units.FirstOrDefault(c => c.Id == u.UnitId)?.Title ?? u.UnitId, u.Status, u.Attempts, u.Reason)).ToList());

        FinalReview review;
        try
        {
            var response = await architect.CompleteAsync(new ProviderRequest(model, ReviewPrompt.System, prompt), ct);
            RecordArchitectCall(request.RunId, units.Count > 0 ? units[^1].UnitId : "", response, budget, CallPurposes.FinalReview);
            request.Artifacts.WriteLog("final-review.reply.md", response.Text);
            review = ReviewPrompt.Parse(response.Text, response.Model);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.ClientModel.ClientResultException)
        {
            review = new FinalReview(ReviewVerdict.Unavailable, $"the architect call failed: {ex.Message}", [], model);
        }

        request.Artifacts.Append("review", new { verdict = review.Verdict.ToString(), review.Summary, review.Concerns, review.Model });
        if (review.Verdict != ReviewVerdict.Approve)
        {
            observer.Warning($"Final review: {review.Verdict}. {review.Summary}");
        }

        return review;
    }

    /// <summary>
    /// Runs one unit to a verdict. The classic loop (worker → gates → supervisor → retry / takeover / fail) is unchanged
    /// for strategies 1-4. With adaptive supervision the unit is re-read from the plan on every attempt (it may have been
    /// revised), the worker's report is read, the architect is called on deterministic triggers, oversized units are
    /// split, a failed takeover can hand the unit back to the worker, and accepted units are reviewed by risk.
    /// </summary>
    private async Task<UnitOutcome> RunUnitAsync(RunContext ctx, string unitId, CancellationToken ct)
    {
        var request = ctx.Request;
        var policy = request.Policy;
        var artifacts = request.Artifacts;
        var workspace = ctx.Workspace;
        var unit = ctx.Plan.Unit(unitId);
        var state = new UnitState { Unit = unit };
        var adaptive = ctx.Adaptive && architect is not null;
        if (ctx.Gates.Acceptance is { } acceptanceFiles && acceptanceFiles.Add(unit).Count > 0)
        {
            // The architect's tests for this unit go in as their own commit, so they are never part of the worker's diff.
            acceptanceFiles.Enforce(workspace.Path);
            if (!ctx.TestsCommitted.Contains(unit.Id))
            {
                await workspace.CommitAsync($"tokonomy({unit.Id}): acceptance tests", ct);
            }

            artifacts.Append("acceptance", new { unit = unit.Id, files = acceptanceFiles.Paths });
        }

        var unitStart = await workspace.HeadAsync(ct);
        var maxAttempts = request.MaxAttempts ?? int.MaxValue;
        string? feedback = null;
        var track = new UnitTrack();

        // A resumed unit keeps its history: attempt numbers continue, and takeover and intervention caps still hold.
        // Its retry window starts fresh, because the interrupted attempt was never judged.
        if (ctx.History.TryGetValue(unitId, out var history))
        {
            state.Attempts = history.Attempts;
            state.RetryWindowStart = history.Attempts;
            state.Takeovers = history.Takeovers;
            track.Interventions = history.Interventions;
            track.InstructionCorrectionUsed = history.InstructionCorrectionUsed;
        }

        SaveRunState(ctx, RunPhases.Unit, unitId, unitStart);
        ctx.Observer.UnitStarted(unit);
        ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "running", state.Attempts, state.Takeovers);
        _currentUnit = unit.Id;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.Budget.Token);
        while (true)
        {
            // The worker always gets the latest approved revision of its unit, never a stale copy.
            unit = ctx.Plan.Unit(unitId);
            state.Attempts++;
            var brief = BriefWriter.Compile(new BriefInput
            {
                Plan = ctx.Plan.Current,
                Unit = unit,
                Attempt = state.Attempts,
                BriefFileName = request.Executor.BriefFile,
                Feedback = feedback,
                Completed = ctx.Completed,
                Handoffs = ctx.Handoffs,
                Acceptance = ctx.Gates.Acceptance is { Count: > 0 } ? (ctx.Gates.Acceptance.Paths, string.Join(' ', policy.Acceptance.Cmd)) : null,
                Revision = ctx.Plan.Revision,
                Lessons = adaptive ? ctx.Plan.LessonsFor(unitId) : [],
                RequestReport = adaptive && policy.Supervision.WorkerReports,
            });
            artifacts.WriteLog($"{brief.Label}.brief.md", brief.Body);

            await ctx.Watch.BeforeAttemptAsync(ct);
            var reportExisted = File.Exists(Path.Combine(workspace.Path, WorkerReports.FileName));
            ctx.Observer.AttemptStarted(unit, state.Attempts);
            var run = await ctx.Executor.RunAsync(brief, workspace.Path, request.WorkerModel,
                TimeSpan.FromSeconds(policy.Escalation.WorkerTimeoutSeconds), linked.Token);
            RecordReportedUsage(request.RunId, unit.Id, run, ctx.Budget, request.Prices);

            // Before git looks at the worktree (staging a junction is how a link would get into the diff): links,
            // the .git pointer and, for executors that report their commands, files hidden in ignored paths.
            WorkerReport? report;
            (run, report) = await InspectAttemptAsync(ctx.Watch, run, artifacts, brief.Label,
                adaptive && policy.Supervision.WorkerReports && !reportExisted ? workspace.Path : null, ct);
            var violated = run.Fault is { Kind: WorkerFaultKind.WorkspaceViolation };
            if (report is not null)
            {
                ledger.RecordEvent(request.RunId, _clock.UtcNow, "worker_report", System.Text.Json.JsonSerializer.Serialize(new
                {
                    unit = unit.Id,
                    attempt = state.Attempts,
                    status = report.Status.ToString(),
                    reason = report.Reason,
                    decision_required = report.DecisionRequired,
                    needs_architect = report.NeedsArchitect,
                    problems = report.Problems.Count,
                }));
                ctx.Observer.WorkerReported(unit, report);
            }

            var diff = await workspace.DiffAsync(unitStart, ct);
            state.ChangedPaths = diff.ChangedPaths;
            state.DiffLines = diff.TotalLines;
            var unapproved = diff.Dependencies.Unapproved(policy.Escalation.AllowedDependencies);
            state.AddedDependency = unapproved.Any;
            state.AddedDependencyNames = unapproved.Unnamed ? [.. unapproved.Names, "(a lockfile or manifest change with no package name)"] : unapproved.Names;
            if (diff.Patch.Length > 0)
            {
                artifacts.WriteLog($"{brief.Label}.diff", diff.Patch);
            }

            // Gates execute code from the worktree; never run them on an attempt that already tried to escape it.
            List<GateResult> gateResults = [];
            List<string?> gateLogs = [];
            if (!violated)
            {
                if (diff.ChangedPaths.Count > 0)
                {
                    ctx.Observer.GatesStarted(unit);
                }

                try
                {
                    (gateResults, gateLogs) = await RunGatesAsync(request, brief.Label, workspace.Path, diff, ctx.Gates, ctx.Baseline, ctx.Locks, linked.Token,
                        adaptive ? ctx.Plan.LessonsFor(unitId) : []);
                }
                catch (GateSecurityException ex)
                {
                    // The snapshot or the environment's output tried to cross the boundary: same as a workspace violation.
                    violated = true;
                    artifacts.Append("violation", new { attempt = brief.Label, detail = ex.Message });
                    run = run with { Fault = new WorkerFault(WorkerFaultKind.WorkspaceViolation, $"gate environment: {ex.Message}") };
                }
            }

            if (!violated && ctx.Watch.CheckLinks("after the gates ran") is { } gateViolation)
            {
                run = run with { Fault = new WorkerFault(WorkerFaultKind.WorkspaceViolation, gateViolation) };
            }

            var decision = supervisor.Decide(state, run, gateResults, policy);
            var signature = FailureSignature.Of(gateResults);
            var repeated = signature is not null && signature == track.LastFailure;
            track.LastFailure = signature;
            var deviation = adaptive ? PlanDeviation.Detect(unit, diff.ChangedPaths, ctx.AcceptanceDir) : PlanDeviation.None;

            // Adaptive: what the architect should look at, decided by deterministic rules (never for wrong-model or
            // workspace-violation failures, never for decisions a human owns, bounded per unit and per run).
            SupervisionRequest? call = null;
            if (adaptive && !ctx.Budget.Exhausted)
            {
                call = InterventionPolicy.Evaluate(new SupervisionContext
                {
                    Decision = decision,
                    Report = report,
                    AvoidTouched = deviation.AvoidTouched,
                    RepeatedFailure = repeated,
                    UnitInterventions = track.Interventions,
                    RunInterventions = ctx.RunInterventions,
                    Takeovers = state.Takeovers,
                    InstructionCorrectionUsed = track.InstructionCorrectionUsed,
                }, policy.Supervision, policy.Escalation);
            }

            var splitting = adaptive && decision.Kind == DecisionKind.Split && CanDecompose(ctx, unit);
            var takeover = call is null && !splitting && decision.CanTakeOver && request.AllowEscalation && architect is not null &&
                           state.Takeovers < policy.Escalation.MaxTakeoversPerUnit;
            if (!takeover && call is null && !splitting)
            {
                decision = Constrain(decision, request, state, maxAttempts, architect is not null);
            }

            ctx.Observer.AttemptFinished(unit, state.Attempts, run, diff, gateResults, decision);
            var attemptId = ledger.RecordAttempt(new AttemptRecord
            {
                RunId = request.RunId,
                UnitId = unit.Id,
                Number = state.Attempts,
                Executor = ctx.Executor.Name,
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
                revision = ctx.Plan.Revision,
                run.ExitCode,
                run.TimedOut,
                fault = run.Fault is null ? null : new { kind = run.Fault.Kind.ToString(), run.Fault.Detail },
                reported_model = run.ReportedModel,
                audit = run.Audit,
                report = report is null ? null : new { status = report.Status.ToString(), report.Reason, report.Description, report.DecisionRequired },
                deviation = deviation.Any ? deviation.Describe().ToList() : null,
                diff_lines = diff.TotalLines,
                changed = diff.ChangedPaths,
                gates = gateResults.Select(g => new { g.Gate, g.Status, g.NewFailures }),
                decision = decision.Kind,
                decision.Reason,
            });

            if (splitting)
            {
                if (await DecomposeAsync(ctx, unit, decision, diff, gateResults, unitStart, ct) is { } splitNote)
                {
                    ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "split", state.Attempts, state.Takeovers);
                    return new UnitOutcome(unit.Id, UnitOutcome.Split, state.Attempts, splitNote) { Interventions = track.Interventions };
                }

                decision = Constrain(decision, request, state, maxAttempts, architect is not null);
            }

            if (call is not null)
            {
                var step = await SuperviseAsync(ctx, unitId, state, track, call, decision, report, diff, gateResults, deviation, unitStart, [], ct);
                switch (step.Kind)
                {
                    case StepKind.Resume:
                        feedback = step.Feedback;
                        state.RetryWindowStart = state.Attempts;
                        track.LastFailure = null;
                        continue;
                    case StepKind.Accepted:
                        return Done(UnitOutcome.Accepted, step.Note);
                    case StepKind.Replaced:
                        ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "split", state.Attempts, state.Takeovers);
                        return new UnitOutcome(unit.Id, UnitOutcome.Split, state.Attempts, step.Note) { Interventions = track.Interventions };
                    case StepKind.Human or StepKind.Failed:
                        await workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
                        return Done(UnitOutcome.Failed, step.Note);
                }

                // Proceed: no action (or no usable decision): the deterministic decision stands, with the classic
                // takeover as the floor so adaptive supervision never rescues less than strategy 4.
                takeover = decision.CanTakeOver && state.Takeovers < policy.Escalation.MaxTakeoversPerUnit;
                if (!takeover)
                {
                    decision = Constrain(decision, request, state, maxAttempts, architectAvailable: true);
                }
            }

            if (takeover)
            {
                var result = await TakeOverAsync(ctx, unit, unitStart, state, decision, diff, gateResults, ct, adaptive ? track : null);
                ctx.Observer.TakeoverFinished(unit, result.Success, result.Note);
                if (result.Success)
                {
                    return Done(UnitOutcome.Accepted, $"architect takeover: {result.Note}");
                }

                await workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
                if (adaptive && policy.Supervision.ResumeAfterFailedTakeover && state.AttemptsInWindow < maxAttempts &&
                    track.Interventions < policy.Supervision.MaxInterventionsPerUnit && ctx.RunInterventions < policy.Supervision.MaxInterventionsPerRun)
                {
                    // Recovery: the unit restarts from its verified start; the worker resumes with the architect's findings.
                    RecordRecovery(ctx, unit, decision, result.Note);
                    track.Interventions++;
                    ctx.RunInterventions++;
                    feedback = $"The architect tried to finish this unit and its fix did not pass: {result.Note}\nThe unit was reset to its start. {decision.Reason}";
                    state.RetryWindowStart = state.Attempts;
                    track.LastFailure = null;
                    continue;
                }

                return Done(UnitOutcome.Failed, $"{decision.Reason}; takeover failed: {result.Note}");
            }

            switch (decision.Kind)
            {
                case DecisionKind.Accept:
                    if (adaptive)
                    {
                        var verdict = await ReviewAsync(ctx, unit, state, track, report, diff, gateResults, deviation, unitStart, ct);
                        switch (verdict.Kind)
                        {
                            case StepKind.Resume:
                                feedback = verdict.Feedback;
                                state.RetryWindowStart = state.Attempts;
                                track.LastFailure = null;
                                continue;
                            case StepKind.Accepted:
                                // Accepted by an architect correction the review escalated to (already committed).
                                return Done(UnitOutcome.Accepted, verdict.Note);
                            case StepKind.Replaced:
                                ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "split", state.Attempts, state.Takeovers);
                                return new UnitOutcome(unit.Id, UnitOutcome.Split, state.Attempts, verdict.Note) { Interventions = track.Interventions };
                            case StepKind.Human or StepKind.Failed:
                                await workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
                                return Done(UnitOutcome.Failed, verdict.Note);
                        }
                    }

                    await workspace.CommitAsync($"tokonomy({unit.Id}): {unit.Title}", ct);
                    return Done(UnitOutcome.Accepted, decision.Reason);

                case DecisionKind.Retry:
                    feedback = BriefWriter.Feedback(decision.Reason, gateResults);
                    continue;

                default:
                    await workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
                    return Done(UnitOutcome.Failed, decision.Reason);
            }
        }

        UnitOutcome Done(string status, string reason)
        {
            ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, status, state.Attempts, state.Takeovers);
            return new UnitOutcome(unit.Id, status, state.Attempts, reason) { Interventions = track.Interventions, Review = track.Review };
        }
    }

    /// <summary>
    /// Applies the workspace checks to a finished attempt and turns findings into a fault. With <paramref name="reportRoot"/>
    /// set, the worker's report file is read (never through a link) and removed, so it is never part of the diff.
    /// </summary>
    private static async Task<(RunResult Run, WorkerReport? Report)> InspectAttemptAsync(
        WorkspaceWatch watch, RunResult run, RunArtifacts artifacts, string label, string? reportRoot, CancellationToken ct)
    {
        if (watch.CheckLinks("after the worker ran") is { } violation)
        {
            artifacts.Append("violation", new { attempt = label, detail = violation });
            return (run with { Fault = new WorkerFault(WorkerFaultKind.WorkspaceViolation, violation) }, null);
        }

        var report = reportRoot is null ? null : ReadWorkerReport(reportRoot, artifacts, label);

        // Only executors that report every shell command (an audit) can be judged here: with no commands run, anything
        // new in ignored paths was written by the worker's file tools on purpose. Others may legitimately create build
        // output, so they are left alone.
        if (run.Audit is { Commands.Count: 0 } && await watch.CheckIgnoredAsync(ct) is { } hidden)
        {
            artifacts.Append("hidden_files", new { attempt = label, detail = hidden });
            return (run.Fault is null ? run with { Fault = new WorkerFault(WorkerFaultKind.PermissionDenied, hidden) } : run, report);
        }

        return (run, report);
    }

    private static WorkerReport? ReadWorkerReport(string root, RunArtifacts artifacts, string label)
    {
        string? text;
        try
        {
            text = WorktreeGuard.ReadAllText(root, WorkerReports.FileName, WorkerReports.MaxBytes);
            if (File.Exists(Path.Combine(root, WorkerReports.FileName)) || WorktreeGuard.IsLink(Path.Combine(root, WorkerReports.FileName)))
            {
                WorktreeGuard.DeleteFile(root, WorkerReports.FileName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorktreeEscapeException)
        {
            artifacts.Append("worker_report", new { attempt = label, error = ex.Message });
            return null;
        }

        if (text is null)
        {
            return null;
        }

        artifacts.WriteLog($"{label}.report.json", text);
        var report = WorkerReports.Parse(text, path =>
        {
            try
            {
                var full = WorktreeGuard.ResolveInside(root, path);
                return File.Exists(full) || Directory.Exists(full);
            }
            catch (WorktreeEscapeException)
            {
                return false;
            }
        });
        artifacts.Append("worker_report", new
        {
            attempt = label,
            status = report?.Status.ToString(),
            report?.Reason,
            report?.DecisionRequired,
            problems = report?.Problems,
            unreadable = report is null,
        });
        return report;
    }

    /// <summary>Runs every command gate, then the locked-region check once an earlier takeover created locks, then the lessons check.</summary>
    private static async Task<(List<GateResult> Results, List<string?> Logs)> RunGatesAsync(
        RunRequest request, string label, string workdir, UnitDiff diff, GateSession gates,
        Dictionary<string, GateRun> baseline, List<LockedRegion> locks, CancellationToken ct, IReadOnlyList<Lesson>? lessons = null)
    {
        var results = new List<GateResult>();
        var logs = new List<string?>();
        if (diff.ChangedPaths.Count == 0)
        {
            return (results, logs);
        }

        foreach (var (gate, gateRun, log) in await gates.RunAllAsync(label, n => $"{label}.gate{n}.log", ct))
        {
            results.Add(CommandGate.Evaluate(gateRun, baseline.GetValueOrDefault(gate.Name)));
            logs.Add(log);
        }

        if (request.UseGates && locks.Count > 0)
        {
            results.Add(LockedRegionGate.Evaluate(workdir, locks));
            logs.Add(null);
        }

        // A corrected mistake made again: lessons with a forbidden pattern are checked against the lines this unit adds.
        if (request.UseGates && lessons is { Count: > 0 } && lessons.Any(l => l.ForbiddenPattern is not null))
        {
            var violations = LessonCheck.Violations(lessons, diff.Patch);
            results.Add(new GateResult
            {
                Gate = LessonCheck.GateName,
                Status = violations.Count == 0 ? GateStatus.Passed : GateStatus.Failed,
                Output = violations.Count == 0 ? "" : "A mistake the architect already corrected was made again:\n" + string.Join('\n', violations),
            });
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
        RunContext ctx, WorkUnit unit, string unitStart, UnitState state, Decision decision, UnitDiff diff, List<GateResult> gateResults, CancellationToken ct,
        UnitTrack? track = null)
    {
        var request = ctx.Request;
        var policy = request.Policy;
        var artifacts = request.Artifacts;
        var model = request.ArchitectModel ?? policy.Models.Architect;
        state.Takeovers++;
        SaveRunState(ctx, RunPhases.Takeover, unit.Id, unitStart);
        ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "takeover", state.Attempts, state.Takeovers);
        ctx.Observer.Stage($"Takeover: the architect ({model}) steps in on {unit.Id} ({decision.Reason})");

        var files = ReadRelevantFiles(ctx.Workspace.Path, diff.ChangedPaths.Concat(unit.Files));
        var repoFiles = await ListRepoFilesAsync(ctx.Workspace.Path, ct);
        var failure = BriefWriter.Feedback(decision.Reason, gateResults);

        string? problem = null;
        TakeoverReply? reply = null;
        ApplyResult? applied = null;
        for (var ask = 1; ask <= 2; ask++)
        {
            var prompt = TakeoverPrompt.Build(ctx.Plan.Current, unit, state.Attempts, decision.Reason, diff.Patch, failure, files, repoFiles, ctx.Locks, problem);
            ProviderResponse response;
            try
            {
                response = await architect!.CompleteAsync(new ProviderRequest(model, TakeoverPrompt.System, prompt), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.ClientModel.ClientResultException)
            {
                return new TakeoverResult(false, $"the architect call failed: {ex.Message}");
            }

            RecordArchitectCall(request.RunId, unit.Id, response, ctx.Budget, CallPurposes.Takeover);
            artifacts.WriteLog($"{unit.Id}-takeover{ask}.reply.md", response.Text);
            if (ctx.Budget.Exhausted)
            {
                return new TakeoverResult(false, "budget exhausted");
            }

            reply = TakeoverPrompt.Parse(response.Text);
            applied = EditApplier.Apply(ctx.Workspace.Path, reply.Edits);
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

        var verified = await VerifyArchitectEditAsync(ctx, unit, unitStart, state, model, reply.Edits, applied, "takeover", decision.Trigger, ct);
        if (!verified.Ok)
        {
            return new TakeoverResult(false, verified.Note);
        }

        var summary = reply.Summary ?? "the architect corrected this unit";
        if (ctx.Adaptive && track is not null)
        {
            // Strategy 5: the classic takeover is architect-written too, so risky ones get the same independent review.
            var after = await ctx.Workspace.DiffAsync(unitStart, ct);
            var reviewed = await ReviewAsync(ctx, unit, state, track, null, after, verified.Gates,
                PlanDeviation.Detect(unit, after.ChangedPaths, ctx.AcceptanceDir), unitStart, ct, architectChange: true);
            if (reviewed.Kind != StepKind.Proceed)
            {
                return new TakeoverResult(false, $"the takeover passed the gates but the independent review did not accept it: {reviewed.Note}{reviewed.Feedback}");
            }
        }

        await CommitArchitectEditAsync(ctx, unit, applied.Files, summary, reply.Why ?? decision.Reason, LocksOf(reply.Edits), "[architect takeover]", ct);
        return new TakeoverResult(true, summary);
    }

    /// <summary>
    /// Re-runs every gate on an architect edit that applied cleanly and records the result as an architect attempt.
    /// <paramref name="kind"/> is <c>takeover</c> (L3) or <c>correction</c> (L2); reasons start with it.
    /// </summary>
    private async Task<(bool Ok, string Note, List<GateResult> Gates)> VerifyArchitectEditAsync(
        RunContext ctx, WorkUnit unit, string unitStart, UnitState state, string model, IReadOnlyList<EditBlock> edits, ApplyResult applied,
        string kind, TakeoverTrigger trigger, CancellationToken ct)
    {
        var request = ctx.Request;
        var artifacts = request.Artifacts;
        var newDiff = await ctx.Workspace.DiffAsync(unitStart, ct);
        if (newDiff.Patch.Length > 0)
        {
            artifacts.WriteLog($"{unit.Id}-{kind}.diff", newDiff.Patch);
        }

        List<GateResult> after;
        List<string?> afterLogs;
        try
        {
            (after, afterLogs) = await RunGatesAsync(request, $"{unit.Id}-{kind}", ctx.Workspace.Path, newDiff, ctx.Gates, ctx.Baseline, ctx.Locks, ct,
                ctx.Adaptive ? ctx.Plan.LessonsFor(unit.Id) : null);
        }
        catch (GateSecurityException ex)
        {
            return (false, $"gate environment violation: {ex.Message}", []);
        }

        if (ctx.Watch.CheckLinks($"after the {kind} gates ran") is { } violation)
        {
            return (false, $"workspace violation: {violation}", after);
        }

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
            Reason = blocking.Count == 0 ? $"{kind}: gates pass" : $"{kind}: gate(s) still failing: {string.Join(", ", blocking)}",
        });
        for (var i = 0; i < after.Count; i++)
        {
            ledger.RecordGateResult(attemptId, after[i], afterLogs[i]);
        }

        artifacts.Append(kind, new
        {
            unit = unit.Id,
            trigger,
            files = applied.Files,
            gates = after.Select(g => new { g.Gate, g.Status }),
            ok = blocking.Count == 0,
        });
        return blocking.Count == 0
            ? (true, "", after)
            : (false, $"gate(s) still failing after the architect's edit: {string.Join(", ", blocking)}", after);
    }

    /// <summary>Locks (for a takeover), writes the handoff for the next workers and commits the unit.</summary>
    private static async Task CommitArchitectEditAsync(
        RunContext ctx, WorkUnit unit, IReadOnlyList<string> files, string summary, string why, IReadOnlyList<LockedRegion> newLocks, string label, CancellationToken ct)
    {
        var request = ctx.Request;
        ctx.Locks.AddRange(newLocks);
        var handoff = new Handoff
        {
            UnitId = unit.Id,
            UnitTitle = unit.Title,
            Files = files,
            Summary = summary,
            Why = why,
            Locked = newLocks,
            MustFollow = ctx.Plan.Current.Rules.Select(r => $"{r.Id}: {r.Text}").ToList(),
        };
        ctx.Handoffs.Add(handoff);
        var next = ctx.Plan.Remaining().FirstOrDefault(u => u.Id != unit.Id);
        var yaml = HandoffWriter.ToYaml(handoff, next is null ? null : $"{next.Id}: {next.Title}");
        request.Artifacts.WriteFile("handoff.yaml", yaml);
        request.Artifacts.WriteFile($"handoff-{unit.Id}.yaml", yaml);
        await ctx.Workspace.CommitAsync($"tokonomy({unit.Id}): {unit.Title} {label}", ct);
    }

    private static List<LockedRegion> LocksOf(IReadOnlyList<EditBlock> edits) => edits
        .Where(e => !string.IsNullOrWhiteSpace(e.Replace))
        .Select(e => new LockedRegion(e.Path.Replace('\\', '/').TrimStart('/'), e.Replace))
        .ToList();

    /// <summary>The current text of the given files, read through the guard (this text is sent to the architect, so a link to e.g. ~/.ssh is never followed).</summary>
    private static Dictionary<string, string> ReadRelevantFiles(string root, IEnumerable<string> paths)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths.Select(PlanPaths.Normalize).OfType<string>().Where(p => !PlanPaths.IsGlob(p)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (WorktreeGuard.ReadAllText(root, path, maxBytes: 200_000) is { } text)
                {
                    files[path] = text;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorktreeEscapeException)
            {
            }
        }

        return files;
    }

    private async Task<string[]> ListRepoFilesAsync(string workdir, CancellationToken ct) =>
        (await _git.RunAsync(workdir, ["ls-files", "--cached", "--others", "--exclude-standard"], ct))
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Records usage the executor reported itself (not metered by the proxy); always marked estimated.</summary>
    private void RecordReportedUsage(string runId, string unitId, RunResult run, BudgetGuard budget, PriceTable prices)
    {
        foreach (var usage in run.Usage)
        {
            // Tokens only (Antigravity reports no cost): use the configured price, so the cost is an API-equivalent figure
            // instead of zero. It stays marked estimated.
            // Executors report cache reads separately from fresh input, while the price table expects the total prompt size.
            var usd = usage.Usd ?? prices.Cost(usage.Model, usage.InputTokens + usage.CachedTokens, usage.OutputTokens, usage.CachedTokens) ?? 0;
            ledger.RecordLlmCall(new LlmCallRecord
            {
                RunId = runId,
                UnitId = unitId,
                Role = LlmRole.Worker,
                Model = usage.Model,
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                CachedTokens = usage.CachedTokens,
                Usd = usd,
                Source = CostSource.Estimated,
            });
            budget.Add(usd);
            NotifyUsage(LlmRole.Worker, usage.Model, usage.InputTokens + usage.CachedTokens, usage.OutputTokens, usd, budget);
        }
    }

    private void NotifyUsage(LlmRole role, string model, long input, long output, decimal usd, BudgetGuard budget)
    {
        try
        {
            _usageObserver?.Usage(role, model, input, output, usd, budget.Spent);
        }
        catch
        {
            // A display problem must never break a run (this can be called from the metering proxy's thread).
        }
    }

    private void RecordArchitectCall(string runId, string unitId, ProviderResponse response, BudgetGuard budget, string purpose)
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
            Purpose = purpose,
        });
        budget.Add(response.Usd ?? 0);
        NotifyUsage(LlmRole.Architect, response.Model, response.InputTokens, response.OutputTokens, response.Usd ?? 0, budget);
    }

    /// <summary>Applies strategy limits on top of the supervisor's policy decision, for escalations that are not taken.</summary>
    internal static Decision Constrain(Decision decision, RunRequest request, UnitState state, int maxAttempts, bool architectAvailable)
    {
        // The size limit belongs to supervision. A strategy that runs without gates is single shot by design and is judged
        // by the hidden acceptance check alone, so a big diff must not discard its work.
        if (decision.Kind == DecisionKind.Split && !request.UseGates)
        {
            return new Decision(DecisionKind.Accept, $"{decision.Reason}; size limit not applied in a strategy without gates");
        }

        if (decision.Kind is DecisionKind.Takeover or DecisionKind.Split)
        {
            if (!request.AllowEscalation)
            {
                return new Decision(DecisionKind.Fail, $"{decision.Reason}; no architect escalation in this strategy");
            }

            if (decision.Kind == DecisionKind.Split)
            {
                return new Decision(DecisionKind.Fail, request.AdaptiveSupervision
                    ? $"{decision.Reason}; the unit could not be split (split limit reached, already a split part, or no usable split)"
                    : $"{decision.Reason}; splitting an oversized unit is not implemented yet in this strategy (strategy 5 splits it)");
            }

            if (!decision.CanTakeOver)
            {
                var approve = decision.Trigger == TakeoverTrigger.NewDependency
                    ? "; approve packages in advance with escalation.allowed_dependencies in .tokonomy/policy.yaml"
                    : "";
                return new Decision(DecisionKind.Fail, $"{decision.Reason}; this needs a human decision, not an architect edit{approve}");
            }

            var why = !architectAvailable ? "no architect provider" : "takeover limit reached for this unit";
            return new Decision(DecisionKind.Fail, $"{decision.Reason}; takeover unavailable ({why})");
        }

        if (decision.Kind == DecisionKind.Retry && state.AttemptsInWindow >= maxAttempts)
        {
            return new Decision(DecisionKind.Fail, $"{decision.Reason}; attempt limit {maxAttempts} reached");
        }

        return decision;
    }

    /// <summary>
    /// The provider for this run, or null when the run executes no gate or prepare command. Throws
    /// <see cref="GateIsolationUnavailableException"/> when none is configured or the configured one cannot run here.
    /// </summary>
    private async Task<IGateExecutionProvider?> ResolveGateProviderAsync(RunRequest request, IRunObserver observer, CancellationToken ct)
    {
        var policy = request.Policy;
        var needed = policy.Prepare.Count > 0 || (request.UseGates && (policy.Acceptance.Enabled || policy.Gates.Any(g => g.Type == GateConfig.CommandType)));
        if (!needed)
        {
            return null;
        }

        var provider = gateProvider ?? GateExecutionFactory.Create(
            policy.GateExecution, runner, request.RepoRoot, GateExecutionFactory.Commands(policy), policy.KeyVariables, policy.Isolation.WorktreeRoot);
        var status = await provider.CheckAsync(ct);
        if (!status.Available)
        {
            throw new GateIsolationUnavailableException($"{provider.Name}: {status.Detail}");
        }

        request.Artifacts.Append("gate_execution", new { provider = provider.Name, isolated = provider.IsIsolated, status.Detail });
        if (!provider.IsIsolated)
        {
            observer.Warning("gate_execution.provider is host-unsafe: gates and prepare steps run code the worker wrote ON THIS MACHINE with your privileges. Use it for development and trusted tasks only.");
        }

        if (provider is WindowsSandboxProvider sandbox)
        {
            foreach (var note in await sandbox.RecoverAsync(includeKept: false, ct))
            {
                observer.Warning($"Cleaned up a gate environment left by an earlier run: {note}");
            }
        }

        return provider;
    }

    /// <summary>
    /// Runs the prepare steps once, on the base commit, before any worker. With an isolated provider they run in their own
    /// environment (network per <c>gate_execution.prepare_network</c>) and what they produce comes back as one opaque
    /// archive that later gate environments unpack; the host never unpacks it. host-unsafe prepares in the worktree.
    /// </summary>
    private static async Task<(bool Ok, string? Dependencies)> PrepareAsync(
        RunRequest request, RunWorkspace workspace, IGateExecutionProvider provider, IRunObserver observer, CancellationToken ct)
    {
        var policy = request.Policy;
        var artifacts = request.Artifacts;
        var network = policy.GateExecution?.PrepareNetwork == GateExecutionConfig.NetworkFull ? NetworkAccess.Full : NetworkAccess.None;
        if (provider.IsIsolated)
        {
            observer.Stage($"Prepare: starting an isolated environment ({provider.Name}, network {network.ToString().ToLowerInvariant()})");
        }

        var env = await provider.CreateAsync(new GateEnvironmentRequest
        {
            RunId = request.RunId,
            Label = "prepare",
            Source = new GateSource(workspace.Path, provider.IsIsolated ? await workspace.SourceFilesAsync(ct) : []),
            Network = network,
        }, ct);
        var failed = false;
        try
        {
            foreach (var (step, i) in policy.Prepare.Select((s, i) => (s, i)))
            {
                observer.Stage($"Prepare: {step.DisplayName}");
                var result = await env.RunAsync(new GateCommand
                {
                    Name = step.DisplayName,
                    Args = step.Cmd,
                    Timeout = TimeSpan.FromSeconds(step.TimeoutSeconds),
                    LogFile = Path.Combine(artifacts.LogsDirectory, $"prepare{i}.log"),
                }, ct);
                artifacts.Append("prepare", new { step = step.DisplayName, result.ExitCode, termination = result.Termination.ToString(), duration_ms = (long)result.Duration.TotalMilliseconds });
                if (!result.Succeeded)
                {
                    failed = true;
                    observer.Warning($"Prepare step '{step.DisplayName}' failed ({result.Termination}, exit {result.ExitCode}): {CommandGate.Tail(result.Output, 800)}");
                    return (false, null);
                }
            }

            string? dependencies = null;
            if (provider.IsIsolated)
            {
                var folder = Path.Combine(artifacts.Directory, "deps");
                Directory.CreateDirectory(folder);
                dependencies = await env.ExportDependenciesAsync(Path.Combine(folder, "deps.tar"), ct);
                artifacts.Append("dependencies", new { bytes = dependencies is null ? 0 : new FileInfo(dependencies).Length, startup_ms = (long)env.StartupTime.TotalMilliseconds });
            }

            return (true, dependencies);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            if (await env.DestroyAsync(failed) is { } left)
            {
                observer.Warning($"Gate environment cleanup: {left}");
            }
        }
    }

    /// <summary>
    /// The command gates of a run and where they execute. Every batch (the baseline, each attempt, each takeover check)
    /// gets a fresh environment built from the worktree's current files and destroyed right after, so nothing one batch
    /// leaves behind (a background process, a planted file) can influence the next.
    /// </summary>
    private sealed class GateSession(
        IGateExecutionProvider? provider, RunRequest request, RunWorkspace workspace, string? dependencies, List<CommandGate> commandGates, IRunObserver observer,
        AcceptanceFiles? acceptance = null, CommandGate? acceptanceGate = null)
    {
        public AcceptanceFiles? Acceptance => acceptance;

        public async Task<List<(CommandGate Gate, GateRun Run, string Log)>> RunAllAsync(
            string label, Func<int, string> logName, CancellationToken ct, bool includeAcceptance = true)
        {
            var results = new List<(CommandGate, GateRun, string)>();
            var gates = includeAcceptance && acceptanceGate is not null && acceptance is { Count: > 0 }
                ? [.. commandGates, acceptanceGate]
                : commandGates;
            if (gates.Count == 0 || provider is null)
            {
                return results;
            }

            // Whatever the worker did to the architect's tests is undone first, so the gate judges the originals.
            if (includeAcceptance && acceptance is { Count: > 0 } && acceptance.Enforce(workspace.Path) is { Count: > 0 } tampered)
            {
                request.Artifacts.Append("acceptance_tamper", new { label, files = tampered });
                observer.Warning($"The worker changed the architect's acceptance tests ({string.Join(", ", tampered.Take(3))}); they were restored before the gates ran.");
            }

            var env = await provider.CreateAsync(new GateEnvironmentRequest
            {
                RunId = request.RunId,
                Label = label,
                Source = new GateSource(workspace.Path, provider.IsIsolated ? await workspace.SourceFilesAsync(ct) : []),
                Network = request.Policy.GateExecution?.NetworkPolicy == GateExecutionConfig.NetworkFull ? NetworkAccess.Full : NetworkAccess.None,
                Dependencies = dependencies,
            }, ct);
            var failed = false;
            try
            {
                if (provider.IsIsolated)
                {
                    request.Artifacts.Append("gate_env", new { label, provider = provider.Name, startup_ms = (long)env.StartupTime.TotalMilliseconds });
                }

                foreach (var (gate, i) in gates.Select((g, i) => (g, i)))
                {
                    var log = Path.Combine(request.Artifacts.LogsDirectory, logName(i));
                    var run = await gate.ExecuteAsync(env, log, ct);
                    failed |= !run.Passed;
                    results.Add((gate, run, log));
                }

                return results;
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                if (await env.DestroyAsync(failed) is { } left)
                {
                    request.Artifacts.Append("cleanup", new { label, warning = left });
                    observer.Warning($"Gate environment cleanup: {left}");
                }
            }
        }
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
                NotifyUsage(LlmRole.Worker, usage.Model, usage.InputTokens, usage.OutputTokens, usage.Usd ?? 0, budget);
            },
            () => budget.Exhausted);
    }

    private sealed class NullObserver : IRunObserver;
}
