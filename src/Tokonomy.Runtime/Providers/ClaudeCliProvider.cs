using System.Text.Json;
using Tokonomy.Core.Net;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Providers;

/// <summary>
/// Architect calls through the Claude Code CLI on the user's own login (no API key): <c>claude -p</c> with no tools,
/// the system prompt from a file and the user prompt on stdin (so size and quoting never hit the command line).
/// Anthropic variables are removed from the child environment so a stray API key cannot switch it to API billing.
/// Cost and tokens come from Claude Code's JSON result and are always reported as estimated.
/// The answer is streamed (<c>stream-json</c>): that gives live progress, and a call that goes silent (a dead connection,
/// for example through a stalled proxy) is ended after <see cref="ArchitectTimeouts.Idle"/> and asked once more.
/// </summary>
public sealed class ClaudeCliProvider(IProcessRunner runner, string command = "claude", TimeSpan? timeout = null, TimeSpan? idleTimeout = null) : IProvider
{
    private static readonly string[] StrippedVariables = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"];

    private readonly TimeSpan _timeout = timeout ?? ArchitectTimeouts.Default;
    private readonly TimeSpan _idle = idleTimeout ?? ArchitectTimeouts.Idle;

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
                command, "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--no-session-persistence", "--disable-slash-commands",
                // No tools, no MCP servers and no user/project settings: the architect only writes text, and every tool
                // definition or settings source Claude Code loads would be billed as input on each call (~35k tokens).
                "--tools", "", "--strict-mcp-config", "--setting-sources", "",
                "--system-prompt-file", systemFile,
            };
            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                args.AddRange(["--model", request.Model]);
            }

            var proxy = ProxySettings.Current.ForProvider("claude");
            var env = EnvironmentBuilder.ForGate(StrippedVariables, null, proxy);
            var outcome = await StreamedProcessCall.RunAsync(runner, new ProcessSpec
            {
                Args = args,
                WorkingDirectory = workDir,
                Environment = env,
                ReplaceEnvironment = true,
                Timeout = _timeout,
                Proxy = proxy,
                StdinText = request.User,
            }, request, StreamedChars, "Claude Code", _idle, ct);

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

    /// <summary>Characters of text or thinking carried by one stream-json line (0 for every other kind of line).</summary>
    internal static int StreamedChars(string line)
    {
        if (!line.Contains("content_block_delta", StringComparison.Ordinal))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("event", out var ev) &&
                ev.TryGetProperty("delta", out var delta))
            {
                foreach (var field in (ReadOnlySpan<string>)["text", "thinking"])
                {
                    if (delta.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString()!.Length;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return 0;
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

            // The model that answered must be the model that was asked for. Claude Code can fall back to another model
            // (overload, plan limits); that must surface as an error, never as an answer attributed to the requested model.
            if (ModelProblem(requestedModel, usage) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            if (usage.Count == 0)
            {
                return new ProviderResponse(text, requestedModel, 0, 0, 0, null, Estimated: true);
            }

            // Label the call with the model that answered (not an auxiliary helper); totals still cover every model used.
            var main = Answering(requestedModel, usage) ?? usage.OrderByDescending(u => u.InputTokens + u.OutputTokens).First();
            decimal? usd = usage.All(u => u.Usd is not null) ? usage.Sum(u => u.Usd!.Value) : null;
            return new ProviderResponse(
                text, main.Model, usage.Sum(u => u.InputTokens), usage.Sum(u => u.OutputTokens), usage.Sum(u => u.CachedTokens), usd, Estimated: true);
        }
    }

    /// <summary>
    /// Null when the answering model matches the request. A full model id must be reported and must match exactly (after
    /// dropping a context suffix like <c>[1m]</c> or a date stamp); an alias such as <c>opus</c> matches any model of that family.
    /// </summary>
    internal static string? ModelProblem(string requested, IReadOnlyList<WorkerUsage> usage)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        var named = usage.Where(u => !string.IsNullOrWhiteSpace(u.Model) && u.Model != "unknown").ToList();
        if (named.Count == 0)
        {
            return IsFullId(requested)
                ? $"Claude Code did not report which model answered; cannot verify the requested model '{requested}'."
                : null;
        }

        // Claude Code also makes small auxiliary calls to a cheaper model on every request, so the model with the most
        // tokens is not necessarily the one that answered. What matters is that the requested model produced output.
        if (Answering(requested, usage) is not null)
        {
            return null;
        }

        var produced = named.Where(u => u.OutputTokens > 0).Select(u => u.Model).DefaultIfEmpty("no model").ToList();
        return $"Requested model '{requested}' but Claude Code answered with {string.Join(", ", produced.Select(m => $"'{m}'"))}. Not falling back silently.";
    }

    /// <summary>The usage entry of the requested model, when it produced output; else null.</summary>
    internal static WorkerUsage? Answering(string requested, IReadOnlyList<WorkerUsage> usage) =>
        string.IsNullOrWhiteSpace(requested)
            ? null
            : usage.Where(u => u.OutputTokens > 0 && !string.IsNullOrWhiteSpace(u.Model) && ModelMatches(requested, u.Model))
                .OrderByDescending(u => u.OutputTokens)
                .FirstOrDefault();

    internal static bool ModelMatches(string requested, string actual)
    {
        var r = NormalizeModel(requested);
        var a = NormalizeModel(actual);
        return string.Equals(r, a, StringComparison.OrdinalIgnoreCase) ||
               (!IsFullId(requested) && a.Contains(r, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFullId(string model) => model.TrimStart().StartsWith("claude-", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeModel(string model)
    {
        var m = model.Trim();
        var bracket = m.IndexOf('[');
        if (bracket >= 0)
        {
            m = m[..bracket];
        }

        return System.Text.RegularExpressions.Regex.Replace(m, @"-\d{8}$", "");
    }

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length <= 600 ? s : "..." + s[^600..];
    }
}
