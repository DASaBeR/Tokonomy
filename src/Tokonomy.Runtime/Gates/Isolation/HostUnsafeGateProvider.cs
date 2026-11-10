using System.Diagnostics;
using Tokonomy.Core.Config;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Gates.Isolation;

/// <summary>
/// <c>host-unsafe</c>: commands run directly in the worktree with the user's privileges and a scrubbed environment (the
/// behavior before gate isolation existed). For development and tests only. It is used only when configured by name,
/// never as a fallback.
/// </summary>
public sealed class HostUnsafeGateProvider(IProcessRunner runner, IReadOnlyCollection<string> alsoRemoveEnv, GateArtifactLimits? limits = null) : IGateExecutionProvider
{
    public string Name => GateExecutionConfig.HostUnsafeProvider;

    public bool IsIsolated => false;

    public Task<ProviderStatus> CheckAsync(CancellationToken ct) =>
        Task.FromResult(ProviderStatus.Ok("NOT ISOLATED: gates run on the host with your privileges"));

    public Task<IGateEnvironment> CreateAsync(GateEnvironmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<IGateEnvironment>(new Environment(runner, request.Source.Root, EnvironmentBuilder.ForGate(alsoRemoveEnv), limits ?? new()));
    }

    private sealed class Environment(IProcessRunner runner, string workdir, Dictionary<string, string> env, GateArtifactLimits limits) : IGateEnvironment
    {
        public TimeSpan StartupTime => TimeSpan.Zero;

        public async Task<GateCommandResult> RunAsync(GateCommand command, CancellationToken ct)
        {
            if (command.ReportPath is not null)
            {
                try
                {
                    WorktreeGuard.DeleteFile(workdir, command.ReportPath);   // a stale report must not count as this run's
                }
                catch (WorktreeEscapeException ex)
                {
                    throw new GateSecurityException($"gate '{command.Name}': its report path leads through a link ({ex.Message})");
                }
            }

            var stopwatch = Stopwatch.StartNew();
            ProcessOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(new ProcessSpec
                {
                    Args = command.Args,
                    WorkingDirectory = workdir,
                    Environment = env,
                    ReplaceEnvironment = true,
                    Timeout = command.Timeout,
                    StdoutFile = command.LogFile,
                    MaxCaptureChars = 64 * 1024,
                    MaxFileBytes = limits.MaxLogKb * 1024L,
                }, ct);
            }
            catch (ExecutableNotFoundException ex)
            {
                return new GateCommandResult { Termination = GateTermination.NotFound, Output = ex.Message, Duration = stopwatch.Elapsed };
            }

            string? report = null;
            if (command.ReportPath is not null && command.ReportDestination is not null)
            {
                if (File.Exists(command.ReportDestination))
                {
                    File.Delete(command.ReportDestination);
                }

                // Through the guard: a test can replace its report with a link to any file the user can read.
                try
                {
                    var copied = WorktreeGuard.CopyFileOut(workdir, command.ReportPath, command.ReportDestination, limits.MaxReportMb * 1024L * 1024);
                    report = copied is > 0 ? command.ReportDestination : null;
                }
                catch (WorktreeEscapeException ex)
                {
                    throw new GateSecurityException($"gate '{command.Name}': its report is not a plain file in the worktree ({ex.Message})");
                }
            }

            return new GateCommandResult
            {
                Termination = outcome.TimedOut ? GateTermination.TimedOut : GateTermination.Completed,
                ExitCode = outcome.TimedOut ? null : outcome.ExitCode,
                Output = (outcome.Stdout + "\n" + outcome.Stderr).Trim(),
                ReportFile = report,
                Duration = outcome.Duration,
            };
        }

        public Task<string?> ExportDependenciesAsync(string destination, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> DestroyAsync(bool failed = false) => Task.FromResult<string?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
