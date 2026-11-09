using Taskrail.Core.Config;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Taskrail.Runtime.Config;

public enum Severity
{
    Warning,
    Error,
}

public sealed record ConfigIssue(Severity Severity, string File, int? Line, string Message)
{
    public override string ToString() =>
        $"{(Severity == Severity.Error ? "error" : "warning")}: {Path.GetFileName(File)}{(Line is { } l ? $":{l}" : "")}: {Message}";
}

/// <summary>Loaded and validated <c>.taskrail/</c> configuration.</summary>
public sealed class TaskrailConfig
{
    public const string DirectoryName = ".taskrail";
    public const string PolicyFile = "policy.yaml";
    public const string ExecutorsFileName = "executors.yaml";
    public const string PricesFile = "prices.yaml";
    public const string LedgerFile = "ledger.db";

    public required string ConfigDirectory { get; init; }
    public required Policy Policy { get; init; }
    public required ExecutorsFile Executors { get; init; }
    public required IReadOnlyList<ConfigIssue> Issues { get; init; }

    /// <summary>The repository root (parent of <c>.taskrail/</c>).</summary>
    public string RepoRoot => Path.GetDirectoryName(Path.GetFullPath(ConfigDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;

    public string LedgerPath => Path.Combine(ConfigDirectory, LedgerFile);

    public bool HasErrors => Issues.Any(i => i.Severity == Severity.Error);
}

public static class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>Finds <c>.taskrail/</c> in <paramref name="startDirectory"/> or any parent.</summary>
    public static string? FindConfigDirectory(string startDirectory)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDirectory)); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, TaskrailConfig.DirectoryName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static TaskrailConfig Load(string configDirectory)
    {
        var issues = new List<ConfigIssue>();
        var policyPath = Path.Combine(configDirectory, TaskrailConfig.PolicyFile);
        var executorsPath = Path.Combine(configDirectory, TaskrailConfig.ExecutorsFileName);

        var policy = Parse<Policy>(policyPath, issues) ?? new Policy();
        var executors = Parse<ExecutorsFile>(executorsPath, issues) ?? new ExecutorsFile();

        if (!issues.Any(i => i.Severity == Severity.Error))
        {
            issues.AddRange(Validate(policy, policyPath));
            issues.AddRange(Validate(executors, executorsPath));
            if (!executors.Executors.ContainsKey(policy.Executors.Default))
            {
                issues.Add(new(Severity.Error, policyPath, null,
                    $"executors.default '{policy.Executors.Default}' is not defined in {TaskrailConfig.ExecutorsFileName}."));
            }
        }

        return new TaskrailConfig { ConfigDirectory = configDirectory, Policy = policy, Executors = executors, Issues = issues };
    }

    internal static T? Parse<T>(string path, List<ConfigIssue> issues)
        where T : class
    {
        if (!File.Exists(path))
        {
            issues.Add(new(Severity.Error, path, null, "file not found (run `taskrail init`)."));
            return null;
        }

        try
        {
            return Deserializer.Deserialize<T>(File.ReadAllText(path)) ?? Activator.CreateInstance<T>();
        }
        catch (YamlException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            issues.Add(new(Severity.Error, path, (int)ex.Start.Line, StripMark(message)));
            return null;
        }
    }

    public static IEnumerable<ConfigIssue> Validate(Policy policy, string file)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (string.IsNullOrWhiteSpace(policy.Models.Architect))
        {
            yield return new(Severity.Warning, file, null, "models.architect is not set; `plan` and `run` will refuse to start.");
        }

        if (string.IsNullOrWhiteSpace(policy.Models.Worker))
        {
            yield return new(Severity.Warning, file, null, "models.worker is not set; `run` will refuse to start.");
        }

        if (!Uri.TryCreate(policy.Provider.BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            yield return new(Severity.Error, file, null, $"provider.base_url '{policy.Provider.BaseUrl}' is not an http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(policy.Provider.ApiKeyEnv))
        {
            yield return new(Severity.Error, file, null, "provider.api_key_env must name an environment variable.");
        }
        else if (LooksLikeSecret(policy.Provider.ApiKeyEnv))
        {
            yield return new(Severity.Error, file, null,
                "provider.api_key_env looks like an API key. Put the *name* of an environment variable here, never the key.");
        }

        for (var i = 0; i < policy.Gates.Count; i++)
        {
            var gate = policy.Gates[i];
            switch (gate.Type)
            {
                case GateConfig.CommandType when gate.Cmd.Count == 0 || string.IsNullOrWhiteSpace(gate.Cmd[0]):
                    yield return new(Severity.Error, file, null, $"gates[{i}]: command gate needs a non-empty cmd array.");
                    break;
                case GateConfig.CommandType when gate.Cmd.Count == 1 && gate.Cmd[0].Contains(' '):
                    yield return new(Severity.Warning, file, null,
                        $"gates[{i}]: cmd '{gate.Cmd[0]}' looks like a shell string. Use an argument array, e.g. [\"ruff\", \"check\", \".\"].");
                    break;
                case GateConfig.CommandType or GateConfig.LockedRegionsType:
                    break;
                default:
                    yield return new(Severity.Error, file, null, $"gates[{i}]: unknown type '{gate.Type}' (expected command or locked_regions).");
                    break;
            }

            if (gate.TimeoutSeconds <= 0)
            {
                yield return new(Severity.Error, file, null, $"gates[{i}]: timeout_seconds must be positive.");
            }
        }

        var e = policy.Escalation;
        if (e.MaxRetriesPerUnit < 0)
        {
            yield return new(Severity.Error, file, null, "escalation.max_retries_per_unit must be >= 0.");
        }

        if (e.MaxTakeoversPerUnit < 0)
        {
            yield return new(Severity.Error, file, null, "escalation.max_takeovers_per_unit must be >= 0.");
        }

        if (e.MaxDiffLines <= 0)
        {
            yield return new(Severity.Error, file, null, "escalation.max_diff_lines must be positive.");
        }

        if (e.WorkerTimeoutSeconds <= 0)
        {
            yield return new(Severity.Error, file, null, "escalation.worker_timeout_seconds must be positive.");
        }

        var b = policy.Budget;
        if (b.MaxUsdPerTask <= 0)
        {
            yield return new(Severity.Error, file, null, "budget.max_usd_per_task must be positive.");
        }

        if (b.MaxUsdPerRun is <= 0)
        {
            yield return new(Severity.Error, file, null, "budget.max_usd_per_run must be positive when set.");
        }

        if (b.MaxArchitectTokenShare is <= 0 or > 1)
        {
            yield return new(Severity.Error, file, null, "budget.max_architect_token_share must be in (0, 1].");
        }
    }

    public static IEnumerable<ConfigIssue> Validate(ExecutorsFile executors, string file)
    {
        ArgumentNullException.ThrowIfNull(executors);

        if (executors.Executors.Count == 0)
        {
            yield return new(Severity.Error, file, null, "no executors defined.");
        }

        foreach (var (name, def) in executors.Executors)
        {
            if (def.Cmd.Count == 0)
            {
                yield return new(Severity.Error, file, null, $"executors.{name}: cmd must be a non-empty argument array.");
            }

            if (string.IsNullOrWhiteSpace(def.BriefFile) || Path.IsPathRooted(def.BriefFile) || def.BriefFile.Contains(".."))
            {
                yield return new(Severity.Error, file, null, $"executors.{name}: brief_file must be a relative path inside the repo.");
            }

            if (def.ModelArgs.Count > 0 && !def.Capabilities.ModelOverride)
            {
                yield return new(Severity.Warning, file, null, $"executors.{name}: model_args set but capabilities.model_override is false.");
            }

            foreach (var (key, value) in def.Env)
            {
                if (LooksLikeSecret(value))
                {
                    yield return new(Severity.Error, file, null,
                        $"executors.{name}.env.{key} looks like a literal secret. Workers only receive the per-run proxy token.");
                }
            }
        }
    }

    internal static bool LooksLikeSecret(string value) =>
        value.StartsWith("sk-", StringComparison.Ordinal) ||
        value.StartsWith("sk_", StringComparison.Ordinal) ||
        value.StartsWith("AIza", StringComparison.Ordinal) ||
        (value.Length >= 32 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && value.Any(char.IsDigit) && value.Any(char.IsLetter));

    private static string StripMark(string message)
    {
        // YamlDotNet prefixes "(Line: x, Col: y, Idx: z) - (Line: ...): "; the line is reported separately.
        var idx = message.LastIndexOf("): ", StringComparison.Ordinal);
        return idx >= 0 && message.StartsWith('(') ? message[(idx + 3)..] : message;
    }
}
