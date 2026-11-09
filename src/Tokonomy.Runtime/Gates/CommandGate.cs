using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Gates;

/// <summary>Raw outcome of one gate execution, before comparison with the baseline.</summary>
public sealed record GateRun(string Gate, bool Passed, bool TimedOut, IReadOnlySet<string>? FailingTests, string OutputTail);

/// <summary>Runs a command gate and compares it with the base-commit baseline.</summary>
public sealed class CommandGate(GateConfig config, IProcessRunner runner, IReadOnlyDictionary<string, string> environment)
{
    public const int FeedbackChars = 6000;

    public string Name => config.DisplayName;

    public async Task<GateRun> ExecuteAsync(string workdir, string? logFile, CancellationToken ct)
    {
        var reportPath = config.Report is null ? null : Path.Combine(workdir, config.Report);
        if (reportPath is not null && File.Exists(reportPath))
        {
            File.Delete(reportPath);
        }

        ProcessOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(new ProcessSpec
            {
                Args = config.Cmd,
                WorkingDirectory = workdir,
                Environment = environment,
                ReplaceEnvironment = true,
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
                StdoutFile = logFile,
                MaxCaptureChars = 64 * 1024,
            }, ct);
        }
        catch (ExecutableNotFoundException ex)
        {
            return new GateRun(Name, false, false, null, ex.Message);
        }

        var failing = reportPath is null ? null : TestReportParser.TryParseFailures(reportPath);
        var output = (outcome.Stdout + "\n" + outcome.Stderr).Trim();
        if (outcome.TimedOut)
        {
            output += $"\n[tokonomy] gate timed out after {config.TimeoutSeconds}s";
        }

        return new GateRun(Name, outcome.Succeeded, outcome.TimedOut, failing, Tail(output, FeedbackChars));
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
