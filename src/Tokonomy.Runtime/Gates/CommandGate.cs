using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Gates.Isolation;

namespace Tokonomy.Runtime.Gates;

/// <summary>Raw outcome of one gate execution, before comparison with the baseline.</summary>
public sealed record GateRun(string Gate, bool Passed, bool TimedOut, IReadOnlySet<string>? FailingTests, string OutputTail);

/// <summary>
/// Runs a command gate in a gate environment (see <see cref="IGateExecutionProvider"/>) and compares it with the
/// base-commit baseline. The gate never touches the process API itself: where the command runs is the provider's job.
/// </summary>
public sealed class CommandGate(GateConfig config)
{
    public const int FeedbackChars = 6000;

    public string Name => config.DisplayName;

    public async Task<GateRun> ExecuteAsync(IGateEnvironment environment, string? logFile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var reportCopy = config.Report is null
            ? null
            : logFile is not null ? logFile + ".report" : Path.Combine(Path.GetTempPath(), $"tokonomy-report-{Guid.NewGuid():N}.xml");
        try
        {
            var result = await environment.RunAsync(new GateCommand
            {
                Name = Name,
                Args = config.Cmd,
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
                ReportPath = config.Report,
                LogFile = logFile,
                ReportDestination = reportCopy,
            }, ct);

            var failing = result.ReportFile is null ? null : TestReportParser.TryParseFailures(result.ReportFile);
            var output = result.Output;
            var timedOut = result.Termination == GateTermination.TimedOut;
            if (timedOut)
            {
                output += $"\n[tokonomy] gate timed out after {config.TimeoutSeconds}s";
            }

            return new GateRun(Name, result.Succeeded, timedOut, failing, Tail(output, FeedbackChars));
        }
        finally
        {
            if (logFile is null && reportCopy is not null && File.Exists(reportCopy))
            {
                File.Delete(reportCopy);
            }
        }
    }

    /// <summary>
    /// Only failures that are new relative to the baseline block. Without per-test data, a gate that already failed
    /// at baseline is downgraded to <see cref="GateStatus.PreExisting"/>. A missing report where the baseline had one
    /// counts as a new failure (usually a build break).
    /// </summary>
    public static GateResult Evaluate(GateRun attempt, GateRun? baseline)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Passed)
        {
            return new GateResult { Gate = attempt.Gate, Status = GateStatus.Passed, BaselinePassed = baseline?.Passed };
        }

        if (baseline is null || baseline.Passed)
        {
            return new GateResult
            {
                Gate = attempt.Gate,
                Status = GateStatus.Failed,
                BaselinePassed = baseline?.Passed,
                NewFailures = attempt.FailingTests?.Order(StringComparer.Ordinal).ToList() ?? [],
                Output = attempt.OutputTail,
            };
        }

        if (baseline.FailingTests is { } before)
        {
            if (attempt.FailingTests is not { } after)
            {
                return new GateResult
                {
                    Gate = attempt.Gate,
                    Status = GateStatus.Failed,
                    BaselinePassed = false,
                    Output = attempt.OutputTail + "\n[tokonomy] test report missing (baseline had one); likely a build failure.",
                };
            }

            var added = after.Except(before).Order(StringComparer.Ordinal).ToList();
            return new GateResult
            {
                Gate = attempt.Gate,
                Status = added.Count == 0 ? GateStatus.PreExisting : GateStatus.Failed,
                BaselinePassed = false,
                NewFailures = added,
                Output = attempt.OutputTail,
            };
        }

        return new GateResult { Gate = attempt.Gate, Status = GateStatus.PreExisting, BaselinePassed = false, Output = attempt.OutputTail };
    }

    internal static string Tail(string text, int max) => text.Length <= max ? text : "...\n" + text[^max..];
}
