using System.Text;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Executors;

/// <summary>Per-run values an executor needs: where the metering proxy is, and where logs go.</summary>
public sealed record ExecutorSession
{
    /// <summary>OpenAI-style base URL ending in <c>/v1</c>, or empty when no proxy runs.</summary>
    public string ProxyUrl { get; init; } = "";

    /// <summary>Bare proxy origin (for clients that append <c>/v1/...</c> themselves).</summary>
    public string ProxyRoot { get; init; } = "";

    public string ProxyToken { get; init; } = "";
    public required string LogDirectory { get; init; }
    public IReadOnlyList<string> ExtraAllowedEnv { get; init; } = [];
}

/// <summary>
/// Runs a coding-agent CLI described by <c>executors.yaml</c>. The brief is written to the agent's native
/// instructions file (appended to any existing content) and every file Tokonomy writes is restored afterwards,
/// so none of it shows up in the unit's diff.
/// </summary>
public sealed class ConfigExecutor(string name, ExecutorDefinition definition, ExecutorSession session, IProcessRunner runner) : IExecutor
{
    public const string BriefMarker = "<!-- tokonomy:brief -->";

    public string Name => name;

    public ExecutorCapabilities Capabilities => definition.Capabilities;

    public async Task<RunResult> RunAsync(Brief brief, string workdir, string? model, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompt"] = brief.Prompt,
            ["model"] = model ?? "",
            ["workdir"] = workdir,
            ["proxy_url"] = session.ProxyUrl,
            ["proxy_root"] = session.ProxyRoot,
            ["proxy_token"] = session.ProxyToken,
        };

        var restore = new List<(string Path, string? Original)>();
        try
        {
            var briefPath = Path.Combine(workdir, definition.BriefFile);
            var existing = File.Exists(briefPath) ? await File.ReadAllTextAsync(briefPath, ct) : null;
            restore.Add((briefPath, existing));
            var body = existing is null ? brief.Body : existing.TrimEnd() + "\n\n" + BriefMarker + "\n\n" + brief.Body;
            await WriteAsync(briefPath, body, ct);

            foreach (var (relative, template) in definition.Files)
            {
                var path = Path.Combine(workdir, relative);
                restore.Add((path, File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null));
                await WriteAsync(path, Substitute(template, vars), ct);
            }

            var args = BuildArgs(definition, vars, model);
            var env = EnvironmentBuilder.ForWorker(
                session.ExtraAllowedEnv,
                definition.Env.ToDictionary(kv => kv.Key, kv => Substitute(kv.Value, vars), StringComparer.Ordinal));

            Directory.CreateDirectory(session.LogDirectory);
            var stdout = Path.Combine(session.LogDirectory, $"{brief.Label}.stdout.log");
            var stderr = Path.Combine(session.LogDirectory, $"{brief.Label}.stderr.log");
            var outcome = await runner.RunAsync(new ProcessSpec
            {
                Args = args,
                WorkingDirectory = workdir,
                Environment = env,
                ReplaceEnvironment = true,
                Timeout = timeout,
                StdoutFile = stdout,
                StderrFile = stderr,
            }, ct);

            return new RunResult
            {
                ExitCode = outcome.ExitCode,
                Duration = outcome.Duration,
                TimedOut = outcome.TimedOut,
                StdoutPath = stdout,
                StderrPath = stderr,
                Usage = ReadUsage(definition.UsageFormat, stdout, model),
            };
        }
        finally
        {
            foreach (var (path, original) in Enumerable.Reverse(restore))
            {
                if (original is null)
                {
                    File.Delete(path);
                }
                else
                {
                    await File.WriteAllTextAsync(path, original, CancellationToken.None);
                }
            }
        }
    }

    private static IReadOnlyList<WorkerUsage> ReadUsage(string? format, string stdoutPath, string? model)
    {
        if (string.IsNullOrWhiteSpace(format) || !File.Exists(stdoutPath))
        {
            return [];
        }

        var text = File.ReadAllText(stdoutPath);
        return format.ToLowerInvariant() switch
        {
            "claude-json" => ClaudeUsageParser.Parse(text),
            "agy-json" => AgyUsageParser.Parse(text, model),
            _ => [],
        };
    }

    internal static List<string> BuildArgs(ExecutorDefinition definition, IReadOnlyDictionary<string, string> vars, string? model)
    {
        var args = definition.Cmd.Select(a => Substitute(a, vars)).ToList();
        if (!string.IsNullOrEmpty(model) && definition.Capabilities.ModelOverride)
        {
            args.AddRange(definition.ModelArgs.Select(a => Substitute(a, vars)));
        }

        return args;
    }

    /// <summary>Replaces <c>{name}</c> placeholders for known names only; anything else (e.g. <c>{env:X}</c>) is left as is.</summary>
    internal static string Substitute(string template, IReadOnlyDictionary<string, string> vars)
    {
        var sb = new StringBuilder(template);
        foreach (var (key, value) in vars)
        {
            sb.Replace("{" + key + "}", value);
        }

        return sb.ToString();
    }

    private static async Task WriteAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
    }
}
