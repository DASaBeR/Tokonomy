using System.Text.Json;
using Tokonomy.Core.Abstractions;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Providers;

/// <summary>
/// Architect calls through the Claude Code CLI on the user's own login (no API key): <c>claude -p</c> with no tools,
/// the system prompt from a file and the user prompt on stdin (so size and quoting never hit the command line).
/// Anthropic variables are removed from the child environment so a stray API key cannot switch it to API billing.
/// Cost and tokens come from Claude Code's JSON result and are always reported as estimated.
/// </summary>
public sealed class ClaudeCliProvider(IProcessRunner runner, string command = "claude", TimeSpan? timeout = null) : IProvider
{
    private static readonly string[] StrippedVariables = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"];

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workDir = Path.Combine(Path.GetTempPath(), "tokonomy-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var systemFile = Path.Combine(workDir, "system.txt");
            await File.WriteAllTextAsync(systemFile, request.System, ct);

            var args = new List<string>
            {
                command, "-p", "--output-format", "json", "--no-session-persistence", "--disable-slash-commands",
                // No tools, no MCP servers and no user/project settings: the architect only writes text, and every tool
                // definition or settings source Claude Code loads would be billed as input on each call (~35k tokens).
                "--tools", "", "--strict-mcp-config", "--setting-sources", "",
                "--system-prompt-file", systemFile,
            };
            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                args.AddRange(["--model", request.Model]);
            }

            var env = EnvironmentBuilder.ForGate(StrippedVariables);
            var outcome = await runner.RunAsync(new ProcessSpec
            {
                Args = args,
                WorkingDirectory = workDir,
                Environment = env,
                ReplaceEnvironment = true,
                Timeout = _timeout,
                StdinText = request.User,
            }, ct);

            if (outcome.TimedOut)
            {
                throw new InvalidOperationException($"Claude Code did not answer within {_timeout.TotalMinutes:0} minutes.");
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
        JsonDocument? doc = null;
        foreach (var candidate in new[] { stdout.Trim(), stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "" })
        {
            try
            {
                doc = JsonDocument.Parse(candidate);
                break;
            }
            catch (JsonException)
            {
            }
        }

        if (doc is null)
        {
            throw new InvalidOperationException($"Claude Code returned no JSON result (exit {exitCode}): {Tail(stderr + stdout)}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
            if (exitCode != 0 || isError || text.Length == 0)
            {
                throw new InvalidOperationException($"Claude Code failed (exit {exitCode}): {Tail(text.Length > 0 ? text : stderr + stdout)}");
            }

            var usage = ClaudeUsageParser.Parse(root.GetRawText());
            if (usage.Count == 0)
            {
                return new ProviderResponse(text, requestedModel, 0, 0, 0, null, Estimated: true);
            }

            var main = usage.OrderByDescending(u => u.InputTokens + u.OutputTokens).First();
            decimal? usd = usage.All(u => u.Usd is not null) ? usage.Sum(u => u.Usd!.Value) : null;
            return new ProviderResponse(
                text, main.Model, usage.Sum(u => u.InputTokens), usage.Sum(u => u.OutputTokens), usage.Sum(u => u.CachedTokens), usd, Estimated: true);
        }
    }

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length <= 600 ? s : "..." + s[^600..];
    }
}
