using Taskrail.Core.Abstractions;
using Taskrail.Core.Model;
using Taskrail.Runtime.Config;
using Taskrail.Runtime.Executors;
using Taskrail.Runtime.Git;
using Taskrail.Runtime.Ledger;
using Taskrail.Runtime.Orchestration;
using Taskrail.Runtime.Process;
using Taskrail.Llm.Providers;

namespace Taskrail.Bench;

public sealed record BenchOptions
{
    public required IReadOnlyList<Strategy> Strategies { get; init; }
    public int Repeat { get; init; } = 3;
    public IReadOnlyCollection<string>? TaskFilter { get; init; }
}

public interface IBenchObserver
{
    void TrialStarted(BenchTask task, Strategy strategy, int repeat, int index, int total) { }
    void TrialFinished(BenchResultRecord result) { }
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
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly GitCli _git = new(runner);

    public async Task<string> RunAsync(Taskset taskset, TaskrailConfig config, BenchOptions options, PriceTable prices, string? upstreamKey,
        IBenchObserver? observer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(taskset);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        var benchId = "bench-" + RunArtifacts.NewRunId(_clock.UtcNow);
        var tasks = taskset.Tasks.Where(t => options.TaskFilter is null || options.TaskFilter.Contains(t.Id)).ToList();
        var trials = (from task in tasks from strategy in options.Strategies from r in Enumerable.Range(1, options.Repeat) select (task, strategy, r)).ToList();
        var cacheRoot = Path.Combine(Path.GetTempPath(), "taskrail", "bench-cache");

        for (var i = 0; i < trials.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (task, strategy, repeat) = trials[i];
            observer?.TrialStarted(task, strategy, repeat, i + 1, trials.Count);
            var result = await RunTrialAsync(benchId, taskset, task, strategy, repeat, config, prices, upstreamKey, cacheRoot, ct);
            ledger.RecordBenchResult(result);
            observer?.TrialFinished(result);
        }

        return benchId;
    }

    private async Task<BenchResultRecord> RunTrialAsync(
        string benchId, Taskset taskset, BenchTask task, Strategy strategy, int repeat, TaskrailConfig config,
        PriceTable prices, string? upstreamKey, string cacheRoot, CancellationToken ct)
    {
        var started = _clock.UtcNow;
        var runId = RunArtifacts.NewRunId(started);
        var clone = Path.Combine(Path.GetTempPath(), "taskrail", "bench", runId);
        var policy = config.Policy;
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

            var planned = await TaskPlanning.PlanAsync(strategy, task.Prompt, clone, baseSha, runId, provider, policy.Models.Architect, ledger, _git, ct);
            var artifacts = new RunArtifacts(config.ConfigDirectory, runId, upstreamKey is null ? [] : [upstreamKey]);
            artifacts.SavePlan(new PlanFile
            {
                RunId = runId,
                Task = task.Prompt,
                Strategy = strategy.Id,
                BaseSha = baseSha,
                Created = started,
                Contract = planned.Contract,
            });

            var orchestrator = new RunOrchestrator(runner, ledger, supervisor, executorFactory, _clock);
            var outcome = await orchestrator.RunAsync(new RunRequest
            {
                RunId = runId,
                Contract = planned.Contract,
                RepoRoot = clone,
                BaseSha = baseSha,
                Policy = policy,
                ExecutorName = policy.Executors.Default,
                Executor = config.Executors.Executors[policy.Executors.Default],
                Artifacts = artifacts,
                WorkerModel = strategy.WorkerIsStrong ? policy.Models.Architect : policy.Models.Worker,
                UseGates = strategy.UseGates,
                AllowEscalation = strategy.AllowEscalation,
                MaxAttempts = strategy.MaxAttempts,
                Prices = prices,
                UpstreamKey = upstreamKey,
                AlreadySpentUsd = planned.ArchitectUsd,
            }, null, ct);

            status = outcome.Status;
            note = outcome.Error;
            (accepted, var acceptanceNote) = await AcceptAsync(taskset, task, clone, outcome.Branch, artifacts, ct);
            note = string.Join("; ", new[] { note, acceptanceNote }.Where(s => !string.IsNullOrEmpty(s)));
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
            Usd = run?.TotalUsd ?? 0,
            CostSource = run?.CostSource ?? CostSource.Estimated,
            Duration = _clock.UtcNow - started,
            Note = string.IsNullOrEmpty(note) ? null : note,
        };
    }

    /// <summary>Checks out the run branch, overlays the hidden acceptance files and runs the acceptance command.</summary>
    private async Task<(bool Accepted, string? Note)> AcceptAsync(Taskset taskset, BenchTask task, string clone, string branch, RunArtifacts artifacts, CancellationToken ct)
    {
        await _git.RunAsync(clone, ["checkout", "--quiet", "--force", branch], ct);
        if (task.Acceptance.Files is { } files)
        {
            CopyDirectory(taskset.Resolve(files), clone);
        }

        try
        {
            var outcome = await runner.RunAsync(new ProcessSpec
            {
                Args = task.Acceptance.Cmd,
                WorkingDirectory = clone,
                Environment = EnvironmentBuilder.ForGate([]),
                ReplaceEnvironment = true,
                Timeout = TimeSpan.FromSeconds(task.Acceptance.TimeoutSeconds),
                StdoutFile = Path.Combine(artifacts.LogsDirectory, "acceptance.log"),
            }, ct);
            artifacts.Append("acceptance", new { outcome.ExitCode, outcome.TimedOut });
            return (outcome.Succeeded, outcome.Succeeded ? null : $"acceptance failed (exit {outcome.ExitCode})");
        }
        catch (ExecutableNotFoundException ex)
        {
            return (false, ex.Message);
        }
    }

    private static bool IsGitRepository(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    /// <summary>Plain directories (e.g. examples inside another repo) become a fresh one-commit repository.</summary>
    private async Task SnapshotDirectoryAsync(string source, string clone, CancellationToken ct)
    {
        CopyDirectory(source, clone);
        await _git.RunAsync(clone, ["init", "--quiet", "-b", "main"], ct);
        await _git.RunAsync(clone, ["add", "-A"], ct);
        await _git.RunAsync(clone, ["-c", "user.name=Taskrail", "-c", "user.email=taskrail@localhost", "commit", "--quiet", "-m", "taskrail bench snapshot"], ct);
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
