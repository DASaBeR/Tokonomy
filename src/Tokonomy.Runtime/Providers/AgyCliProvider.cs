using System.Text;
using Tokonomy.Core.Net;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Providers;

/// <summary>
/// Architect calls through the Antigravity CLI on the user's own login: <c>agy -p</c> in an empty scratch folder that holds
/// the whole prompt as <c>PROMPT.md</c> (the command line cannot carry a large prompt and <c>agy</c> does not read one from
/// stdin). The session may read that folder and nothing else: no writes, no commands, no other paths. The model that
/// answered is checked against the one asked for, and a refused call or an error status fails the call instead of returning text.
/// </summary>
public sealed class AgyCliProvider(IProcessRunner runner, string command = "agy", TimeSpan? timeout = null, string? scratchRoot = null) : IProvider
{
    private const string Instruction =
        "Read the file PROMPT.md in the current directory and do exactly what it says. Use no tool other than reading that file. " +
        "Reply with only the requested answer, with no preamble.";

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = ExecutableResolver.TryResolve(command, Path.GetTempPath())
                       ?? throw new InvalidOperationException($"The Antigravity CLI '{command}' was not found on PATH or in ~/.gemini/bin.");

        // Outside the system temp folder on purpose: agy lets every run read and write temp.
        var root = scratchRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tokonomy", "agy-scratch");
        var workDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var prompt = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(request.System))
            {
                prompt.AppendLine(request.System.Trim()).AppendLine().AppendLine("---").AppendLine();
            }

            prompt.AppendLine(request.User.Trim());
            if (request.JsonOutput)
            {
                prompt.AppendLine().AppendLine("Answer with a single JSON object only, without a code fence.");
            }

            await File.WriteAllTextAsync(Path.Combine(workDir, "PROMPT.md"), prompt.ToString(), new UTF8Encoding(false), ct);

            var permissions = new ExecutorPermissions { Commands = [], AllowGateCommands = false, ScopeFilesToWorktree = true };
            var settings = AgySettings.Build(workDir, permissions, [], tempDirectory: Path.GetTempPath());
            var settingsPath = Path.Combine(workDir, ".agy-settings.json");
            await File.WriteAllTextAsync(settingsPath, settings.Json, new UTF8Encoding(false), ct);

            var args = new List<string>
            {
                resolved, "-p", Instruction, "--output-format", "stream-json", "--settings", settingsPath, "--sandbox",
            };
            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                args.AddRange(["--model", request.Model]);
            }

            var outcome = await runner.RunAsync(new ProcessSpec
            {
                Args = args,
                WorkingDirectory = workDir,
                Environment = EnvironmentBuilder.ForGate([], null, ProxySettings.Current.ForProvider("antigravity")),
                ReplaceEnvironment = true,
                Timeout = _timeout,
                Proxy = ProxySettings.Current.ForProvider("antigravity"),
            }, ct);

            if (outcome.TimedOut)
            {
                throw new InvalidOperationException($"Antigravity did not answer within {_timeout.TotalMinutes:0} minutes.");
            }

            return Parse(outcome.ExitCode, outcome.Stdout, outcome.Stderr, request.Model);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static ProviderResponse Parse(int exitCode, string stdout, string stderr, string requestedModel)
    {
        var report = AgyRunParser.Parse(stdout, requestedModel);
        if (!report.HasResult)
        {
            throw new InvalidOperationException($"Antigravity returned no result (exit {exitCode}): {Tail(stderr + stdout)}");
        }

        if (exitCode != 0 || !string.Equals(report.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) || report.Error is { Length: > 0 })
        {
            throw new InvalidOperationException($"Antigravity failed (status {report.Status}, exit {exitCode}): {Tail(report.Error ?? report.Response)}");
        }

        if (report.DeniedActions.Count > 0 || report.Tools.Any(t => t.Errored))
        {
            throw new InvalidOperationException(
                "Antigravity tried a tool it is not allowed to use while planning (" +
                string.Join(", ", report.DeniedActions.Concat(report.Tools.Where(t => t.Errored).Select(t => t.Name))) + "); the answer was discarded.");
        }

        // A silent fallback to another model must surface as an error, never as an answer attributed to the requested one.
        if (!string.IsNullOrWhiteSpace(requestedModel) && !string.IsNullOrWhiteSpace(report.Model) &&
            !string.Equals(report.Model, requestedModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Antigravity answered with model '{report.Model}' instead of '{requestedModel}'.");
        }

        var text = report.Response.Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("Antigravity returned an empty answer.");
        }

        var usage = report.Usage;
        return new ProviderResponse(
            text, report.Model ?? requestedModel, usage.Sum(u => u.InputTokens), usage.Sum(u => u.OutputTokens), usage.Sum(u => u.CachedTokens), null, Estimated: true);
    }

    private static string Tail(string text) => text.Length <= 400 ? text.Trim() : "..." + text[^400..].Trim();
}
