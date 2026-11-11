using Tokonomy.Core.Abstractions;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Providers;

/// <summary>
/// Runs a coding-agent CLI that streams its answer line by line, for every CLI provider: reports live progress to the
/// caller, ends the call when it goes silent (a dead connection) and asks once more before giving up.
/// </summary>
internal static class StreamedProcessCall
{
    private const int Attempts = 2;

    /// <param name="charsOf">Characters of answer text carried by one output line (0 for lines that carry none).</param>
    public static async Task<ProcessOutcome> RunAsync(
        IProcessRunner runner, ProcessSpec spec, ProviderRequest request, Func<string, int> charsOf, string tool, TimeSpan idle, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            long chars = 0;
            var retrying = attempt > 1;
            var outcome = await runner.RunAsync(spec with
            {
                IdleTimeout = idle,
                OnStdoutLine = request.OnProgress is { } report
                    ? line =>
                    {
                        if (charsOf(line) is var n and > 0)
                        {
                            chars += n;
                            report(new ProviderProgress(chars, retrying));
                        }
                    }
                    : null,
            }, ct);

            if (!outcome.IdledOut)
            {
                return outcome;
            }

            if (attempt >= Attempts)
            {
                throw new InvalidOperationException(
                    $"{tool} stopped sending data for {idle.TotalMinutes:0.#} minutes, twice. The connection looks dead: " +
                    "check your network or proxy (`tokonomy proxy test`) and try again.");
            }

            request.OnProgress?.Invoke(new ProviderProgress(0, Retrying: true));
        }
    }
}
