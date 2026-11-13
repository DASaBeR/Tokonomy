using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Gates.Isolation;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;
using Tokonomy.Llm.Providers;

namespace Tokonomy.Bench;

public sealed record BenchOptions
{
    public required IReadOnlyList<Strategy> Strategies { get; init; }
    public int Repeat { get; init; } = 3;
    public IReadOnlyCollection<string>? TaskFilter { get; init; }

    /// <summary>
    /// Total spend cap across all trials. A trial may spend at most min(budget.max_usd_per_task, what remains), and the
    /// benchmark stops starting trials once less than <see cref="BenchRunner.MinTrialBudgetUsd"/> remains.
    /// </summary>
    public decimal? MaxUsd { get; init; }

    /// <summary>
    /// Paired design: every strategy that uses an architect plan executes the SAME plan for a given task and repeat
    /// (made once, by the first such trial), so differences come from execution, not from planner variance. The
    /// planning cost is charged to every trial that uses the plan, as each would have had to plan on its own.
    /// </summary>
    public bool SharePlans { get; init; }

    /// <summary>
    /// After each trial, an independent architect call judges the final diff against the task alone (not the plan),
    /// identically for every strategy. Recorded as an event, never charged to the trial.
    /// </summary>
    public bool Judge { get; init; }

    /// <summary>Written to <c>runs/&lt;bench-id&gt;.manifest.json</c> for reproducibility (tool versions, code state).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

public interface IBenchObserver
{
    void TrialStarted(BenchTask task, Strategy strategy, int repeat, int index, int total) { }
    void TrialFinished(BenchResultRecord result) { }
    void Stopped(string reason, int skippedTrials) { }
}

/// <summary>
/// Runs every (task, strategy, repeat) trial in a fresh clone, then judges the result branch with the task's
/// hidden acceptance check. Acceptance, not the supervisor's verdict, defines success.
/// </summary>
public sealed class BenchRunner(
    IProcessRunner runner,
    ILedger ledger,
    ISupervisor supervisor,
    IProvider? provider,
    Func<ExecutorSession, IExecutor>? executorFactory = null,
    IClock? clock = null)
{
    /// <summary>Below this remaining budget a new trial is not started: it could not finish a plan and one attempt.</summary>
    public const decimal MinTrialBudgetUsd = 0.01m;

    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly GitCli _git = new(runner);

    public async Task<string> RunAsync(Taskset taskset, TokonomyConfig config, BenchOptions options, PriceTable prices, string? upstreamKey,
        IBenchObserver? observer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(taskset);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        var benchId = "bench-" + RunArtifacts.NewRunId(_clock.UtcNow);
        var tasks = taskset.Tasks.Where(t => options.TaskFilter is null || options.TaskFilter.Contains(t.Id)).ToList();
        var trials = (from task in tasks from strategy in options.Strategies from r in Enumerable.Range(1, options.Repeat) select (task, strategy, r)).ToList();
        var cacheRoot = Path.Combine(Path.GetTempPath(), "tokonomy", "bench-cache");
        WriteManifest(benchId, taskset, config, options, tasks, trials.Count);
        var plans = new Dictionary<(string Task, int Repeat), (PlannedTask Planned, string RunId)>();

        decimal spent = 0;
        for (var i = 0; i < trials.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            decimal? remaining = options.MaxUsd is { } cap ? cap - spent : null;
            if (remaining < MinTrialBudgetUsd)
            {
                observer?.Stopped(
                    string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"total budget ${options.MaxUsd:0.00} reached (${spent:0.0000} spent)"),
                    trials.Count - i);
                break;
            }

            var (task, strategy, repeat) = trials[i];
            var trialCap = remaining is { } r ? Math.Min(config.Policy.Budget.MaxUsdPerTask, r) : config.Policy.Budget.MaxUsdPerTask;
            observer?.TrialStarted(task, strategy, repeat, i + 1, trials.Count);
            var result = await RunTrialAsync(benchId, taskset, task, strategy, repeat, config, trialCap, prices, upstreamKey, cacheRoot,
                options.SharePlans ? plans : null, options.Judge, ct);
            ledger.RecordBenchResult(result);
            spent += result.Usd;
            observer?.TrialFinished(result);
        }

        return benchId;
    }

    /// <summary>The policy with this trial's spend cap applied. Everything else is shared with the original.</summary>
    internal static Policy WithTrialCap(Policy policy, decimal capUsd) => new()
    {
        Models = policy.Models,
        Executors = policy.Executors,
        Provider = policy.Provider,
        Prepare = policy.Prepare,
        Gates = policy.Gates,
        Escalation = policy.Escalation,
        Acceptance = policy.Acceptance,
        ArchitectProvider = policy.ArchitectProvider,
        Planning = policy.Planning,
        Supervision = policy.Supervision,
        Review = policy.Review,
        Isolation = policy.Isolation,
        GateExecution = policy.GateExecution,
        Budget = new BudgetConfig
        {
            MaxUsdPerTask = capUsd,
            MaxUsdPerRun = policy.Budget.MaxUsdPerRun,
            MaxArchitectTokenShare = policy.Budget.MaxArchitectTokenShare,
        },
    };

    private async Task<BenchResultRecord> RunTrialAsync(
        string benchId, Taskset taskset, BenchTask task, Strategy strategy, int repeat, TokonomyConfig config, decimal trialCap,
        PriceTable prices, string? upstreamKey, string cacheRoot,
        Dictionary<(string Task, int Repeat), (PlannedTask Planned, string RunId)>? plans, bool judge, CancellationToken ct)
    {
        decimal sharedPlanUsd = 0;
        var started = _clock.UtcNow;
        var runId = RunArtifacts.NewRunId(started);
        var clone = Path.Combine(Path.GetTempPath(), "tokonomy", "bench", runId);
        var policy = WithTrialCap(config.Policy, trialCap);
        var status = RunStatus.Error;
        string? note = null;
        var accepted = false;

        ledger.StartRun(new RunRecord
        {
            Id = runId,
            Strategy = strategy.Id,
            TaskId = task.Id,
            Repo = task.Repo,
            Status = RunStatus.Planned,
            Started = started,
        });

        try
        {
            var source = task.IsRemote ? await CachedCloneAsync(task.Repo, cacheRoot, ct) : taskset.Resolve(task.Repo);
            if (task.IsRemote || IsGitRepository(source))
            {
                await _git.RunAsync(Path.GetTempPath(), ["clone", "--quiet", "--no-hardlinks", source, clone], ct);
            }
            else
            {
                await SnapshotDirectoryAsync(source, clone, ct);
            }

            var baseSha = await _git.ResolveAsync(clone, task.Base, ct);

            PlannedTask planned;
            if (plans is not null && strategy.UsesArchitectPlan && plans.TryGetValue((task.Id, repeat), out var shared))
            {
                planned = shared.Planned;
                sharedPlanUsd = planned.ArchitectUsd;
                note = $"plan shared from {shared.RunId}";
                ledger.RecordEvent(runId, _clock.UtcNow, "shared_plan", System.Text.Json.JsonSerializer.Serialize(new { from = shared.RunId, usd = planned.ArchitectUsd }));
            }
            else
            {
                planned = await TaskPlanning.PlanAsync(strategy, task.Prompt, clone, baseSha, runId, provider, policy.Models.Architect, ledger, _git, ct, policy.Acceptance,
                    policy: policy);
                if (plans is not null && strategy.UsesArchitectPlan)
                {
                    plans[(task.Id, repeat)] = (planned, runId);
                }
            }

            var artifacts = new RunArtifacts(config.ConfigDirectory, runId, upstreamKey is null ? [] : [upstreamKey]);
            artifacts.SavePlan(new PlanFile
            {
                RunId = runId,
                Task = task.Prompt,
                Strategy = strategy.Id,
                BaseSha = baseSha,
                Created = started,
                Contract = planned.Contract,
                Readiness = strategy.UsesArchitectPlan ? planned.Assessment.Readiness : null,
                Issues = planned.Assessment.Issues.Select(i => i.ToString()).ToList(),
            });

            var orchestrator = new RunOrchestrator(runner, ledger, supervisor, executorFactory, _clock, strategy.AllowEscalation ? provider : null);
            var outcome = await orchestrator.RunAsync(new RunRequest
            {
                RunId = runId,
                Contract = planned.Contract,
                RepoRoot = clone,
                BaseSha = baseSha,
                Policy = policy,
                ExecutorName = policy.Executors.For(strategy.WorkerIsStrong),
                Executor = config.Executors.Executors[policy.Executors.For(strategy.WorkerIsStrong)],
                Artifacts = artifacts,
                WorkerModel = strategy.WorkerIsStrong ? policy.Models.Architect : policy.Models.Worker,
                UseGates = strategy.UseGates,
                AllowEscalation = strategy.AllowEscalation,
                AdaptiveSupervision = strategy.AdaptiveSupervision,
                MaxAttempts = strategy.MaxAttempts,
                Prices = prices,
                UpstreamKey = upstreamKey,
                AlreadySpentUsd = planned.ArchitectUsd,
            }, null, ct);

            status = outcome.Status;
            note = string.Join("; ", new[] { note, outcome.Error }.Where(s => !string.IsNullOrEmpty(s)));
            (accepted, var acceptanceNote) = outcome.Branch.Length == 0
                ? (false, "acceptance not run: the run never created a branch")
                : await AcceptAsync(taskset, task, clone, outcome.Branch, policy, runId, artifacts, ct);
            note = string.Join("; ", new[] { note, acceptanceNote }.Where(s => !string.IsNullOrEmpty(s)));
            if (outcome.Branch.Length > 0)
            {
                await MeasureQualityAsync(task, clone, baseSha, outcome.Branch, artifacts.LoadPlanState()?.Current ?? planned.Contract, runId, policy, judge, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            note = ex.Message;
            ledger.FinishRun(runId, RunStatus.Error, _clock.UtcNow);
        }
        finally
        {
            TryDelete(clone);
        }

        var run = ledger.GetRun(runId);
        return new BenchResultRecord
        {
            BenchId = benchId,
            TaskId = task.Id,
            Strategy = strategy.Id,
            Repeat = repeat,
            RunId = runId,
            RunStatus = status,
            Accepted = accepted,
            Usd = (run?.TotalUsd ?? 0) + sharedPlanUsd,
            CostSource = run?.CostSource ?? CostSource.Estimated,
            Duration = _clock.UtcNow - started,
            Note = string.IsNullOrEmpty(note) ? null : note,
        };
    }

    /// <summary>
    /// Checks out the run branch, overlays the hidden acceptance files and runs the acceptance command through the
    /// configured gate provider: the acceptance tests exercise code the worker wrote, so they get the same isolation
    /// (and the same refusal to fall back to the host) as gates.
    /// </summary>
    private async Task<(bool Accepted, string? Note)> AcceptAsync(
        Taskset taskset, BenchTask task, string clone, string branch, Policy policy, string runId, RunArtifacts artifacts, CancellationToken ct)
    {
        await _git.RunAsync(clone, ["checkout", "--quiet", "--force", branch], ct);
        if (task.Acceptance.Files is { } files)
        {
            CopyDirectory(taskset.Resolve(files), clone);
        }

        IGateExecutionProvider provider;
        try
        {
            provider = GateExecutionFactory.Create(policy.GateExecution, runner, clone, [task.Acceptance.Cmd], policy.KeyVariables, policy.Isolation.WorktreeRoot);
            var status = await provider.CheckAsync(ct);
            if (!status.Available)
            {
                return (false, $"acceptance not run: {provider.Name}: {status.Detail}");
            }
        }
        catch (GateIsolationUnavailableException ex)
        {
            return (false, $"acceptance not run: {ex.Message}");
        }

        var source = new GateSource(clone, provider.IsIsolated ? await RunWorkspace.SourceFilesAsync(_git, clone, ct) : []);
        var env = await provider.CreateAsync(new GateEnvironmentRequest { RunId = runId, Label = "acceptance", Source = source }, ct);
        try
        {
            var result = await env.RunAsync(new GateCommand
            {
                Name = "acceptance",
                Args = task.Acceptance.Cmd,
                Timeout = TimeSpan.FromSeconds(task.Acceptance.TimeoutSeconds),
                LogFile = Path.Combine(artifacts.LogsDirectory, "acceptance.log"),
            }, ct);
            artifacts.Append("acceptance", new { result.ExitCode, termination = result.Termination.ToString(), provider = provider.Name });
            return result.Termination switch
            {
                GateTermination.NotFound => (false, result.Output),
                _ when result.Succeeded => (true, null),
                _ => (false, $"acceptance failed ({result.Termination}, exit {result.ExitCode})"),
            };
        }
        catch (GateSecurityException ex)
        {
            return (false, $"acceptance rejected: {ex.Message}");
        }
        finally
        {
            await env.DestroyAsync();
        }
    }

    internal const string JudgeSystem = """
        You are an independent principal engineer judging a finished change for a benchmark. You did not plan or write it.
        Judge the change against the task and the repository as it actually is. The task description may contain
        INCORRECT assumptions about the repository (for example a helper, module or file that does not exist); a change
        that correctly works around a false assumption is right, and one that invents the missing piece is acceptable
        only if it is clean. Ignore whether automated tests pass (that is measured separately). Look for: requirements of
        the task not met, wrong or risky logic, unrelated modifications, needless dependencies, duplicated logic,
        changes to public behaviour the task did not ask for, and poor structure. Do not raise style nits.

        Answer in exactly this form:

        VERDICT: APPROVE
        SUMMARY: one sentence

        or

        VERDICT: CONCERNS
        SUMMARY: one sentence
        CONCERNS:
        - one concrete problem per line, naming the file
        """;

    /// <summary>
    /// Deterministic and judged quality of a trial's final branch, the same for every strategy: files changed that the
    /// final plan never named (tests excluded), and optionally an independent review of the diff against the task
    /// alone. Stored as a <c>bench_quality</c> event; the judge's tokens are never charged to the trial.
    /// </summary>
    private async Task MeasureQualityAsync(
        BenchTask task, string clone, string baseSha, string branch, Contract plan, string runId, Policy policy, bool judge, CancellationToken ct)
    {
        var changed = (await _git.RunAsync(clone, ["diff", "--name-only", "--no-renames", baseSha, branch], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        int? unplanned = null;
        var expected = plan.Units.SelectMany(u => u.ExpectedFiles).Distinct(StringComparer.Ordinal).ToList();
        if (plan.Units.Count > 0 && expected.Count > 0)
        {
            var all = new WorkUnit { Id = "ALL", Title = "all", Spec = "all", Files = expected };
            unplanned = Tokonomy.Core.Supervision.PlanDeviation.Detect(all, changed, policy.Acceptance.Enabled ? policy.Acceptance.Dir : null).Unplanned.Count;
        }

        string? verdict = null;
        string? summary = null;
        IReadOnlyList<string> concerns = [];
        decimal judgeUsd = 0;
        long judgeTokens = 0;
        if (judge && provider is not null)
        {
            var patch = await _git.RunAsync(clone, ["diff", "--no-renames", baseSha, branch], ct);
            try
            {
                var response = await provider.CompleteAsync(new ProviderRequest(policy.Models.Architect, JudgeSystem,
                    Tokonomy.Llm.Architect.ReviewPrompt.Build(Strategy.SingleUnit(task.Prompt), patch, [])), ct);
                var review = Tokonomy.Llm.Architect.ReviewPrompt.Parse(response.Text, response.Model);
                (verdict, summary, concerns) = (review.Verdict.ToString(), review.Summary, review.Concerns);
                judgeUsd = response.Usd ?? 0;
                judgeTokens = response.InputTokens + response.OutputTokens;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.ClientModel.ClientResultException)
            {
                (verdict, summary) = ("Unavailable", ex.Message);
            }
        }

        ledger.RecordEvent(runId, _clock.UtcNow, "bench_quality", System.Text.Json.JsonSerializer.Serialize(new
        {
            changed = changed.Count,
            unplanned,
            judge = verdict,
            summary,
            concerns,
            judge_usd = judgeUsd,
            judge_tokens = judgeTokens,
        }));
    }

    private void WriteManifest(string benchId, Taskset taskset, TokonomyConfig config, BenchOptions options, IReadOnlyList<BenchTask> tasks, int trials)
    {
        var p = config.Policy;
        var manifest = new
        {
            bench_id = benchId,
            started = _clock.UtcNow,
            taskset = taskset.BaseDirectory,
            tasks = tasks.Select(t => new { t.Id, t.Repo, prompt_sha = Sha(t.Prompt), acceptance = t.Acceptance.Files }),
            strategies = options.Strategies.Select(s => new { s.Id, s.Description }),
            options.Repeat,
            trials,
            options.MaxUsd,
            share_plans = options.SharePlans,
            judge = options.Judge,
            models = new { architect = p.Models.Architect, worker = p.Models.Worker },
            architect_provider = p.ArchitectProvider.Kind,
            executors = new { cheap = p.Executors.Default, strong = p.Executors.Strong },
            temperature = "provider default (not settable through the Claude Code and Antigravity CLIs)",
            planning = p.Planning,
            supervision = p.Supervision,
            review = p.Review,
            escalation = p.Escalation,
            budget = p.Budget,
            gate_execution = p.GateExecution?.Provider,
            gates = p.Gates.Select(g => g.DisplayName),
            prompts = Tokonomy.Llm.Architect.PromptCatalog.Hashes(),
            environment = options.Environment,
        };
        var path = Path.Combine(config.ConfigDirectory, "runs", benchId + ".manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(manifest, RunArtifacts.JsonOptions));
    }

    private static string Sha(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12];

    private static bool IsGitRepository(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    /// <summary>Plain directories (e.g. examples inside another repo) become a fresh one-commit repository.</summary>
    private async Task SnapshotDirectoryAsync(string source, string clone, CancellationToken ct)
    {
        CopyDirectory(source, clone);
        await _git.RunAsync(clone, ["init", "--quiet", "-b", "main"], ct);
        await _git.RunAsync(clone, ["add", "-A"], ct);
        await _git.RunAsync(clone, ["-c", "user.name=Tokonomy", "-c", "user.email=tokonomy@localhost", "commit", "--quiet", "-m", "tokonomy bench snapshot"], ct);
    }

    private async Task<string> CachedCloneAsync(string url, string cacheRoot, CancellationToken ct)
    {
        var name = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16];
        var path = Path.Combine(cacheRoot, name);
        if (Directory.Exists(path))
        {
            await _git.RunAsync(path, ["fetch", "--quiet", "--all", "--tags"], ct, allowFailure: true);
        }
        else
        {
            Directory.CreateDirectory(cacheRoot);
            await _git.RunAsync(cacheRoot, ["clone", "--quiet", "--mirror", url, path], ct);
        }

        return path;
    }

    internal static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            throw new DirectoryNotFoundException($"Acceptance files not found: {from}");
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            // Git marks pack files read-only; clear attributes so deletion works on Windows.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
