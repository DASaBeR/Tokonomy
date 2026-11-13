using Tokonomy.Core.Config;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Tokonomy.Runtime.Config;

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

/// <summary>Loaded and validated <c>.tokonomy/</c> configuration.</summary>
public sealed class TokonomyConfig
{
    public const string DirectoryName = ".tokonomy";
    public const string PolicyFile = "policy.yaml";
    public const string ExecutorsFileName = "executors.yaml";
    public const string PricesFile = "prices.yaml";
    public const string LedgerFile = "ledger.db";

    public required string ConfigDirectory { get; init; }
    public required Policy Policy { get; init; }
    public required ExecutorsFile Executors { get; init; }
    public required IReadOnlyList<ConfigIssue> Issues { get; init; }

    /// <summary>The repository root (parent of <c>.tokonomy/</c>).</summary>
    public string RepoRoot => Path.GetDirectoryName(Path.GetFullPath(ConfigDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;

    public string LedgerPath => Path.Combine(ConfigDirectory, LedgerFile);

    public bool HasErrors => Issues.Any(i => i.Severity == Severity.Error);
}

public static class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>Finds <c>.tokonomy/</c> in <paramref name="startDirectory"/> or any parent.</summary>
    public static string? FindConfigDirectory(string startDirectory)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDirectory)); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, TokonomyConfig.DirectoryName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static TokonomyConfig Load(string configDirectory)
    {
        // Always absolute: run artifacts live under it, and a worker runs with the worktree as its working directory, so
        // a path like examples/.tokonomy/runs/... would not exist for it.
        configDirectory = Path.GetFullPath(configDirectory);
        var issues = new List<ConfigIssue>();
        var policyPath = Path.Combine(configDirectory, TokonomyConfig.PolicyFile);
        var executorsPath = Path.Combine(configDirectory, TokonomyConfig.ExecutorsFileName);

        var policy = Parse<Policy>(policyPath, issues) ?? new Policy();
        var executors = Parse<ExecutorsFile>(executorsPath, issues) ?? new ExecutorsFile();

        if (!issues.Any(i => i.Severity == Severity.Error))
        {
            issues.AddRange(Validate(policy, policyPath));
            issues.AddRange(Validate(executors, executorsPath));
            if (!string.IsNullOrWhiteSpace(policy.Executors.Strong) && !executors.Executors.ContainsKey(policy.Executors.Strong))
            {
                issues.Add(new ConfigIssue(Severity.Error, executorsPath, null,
                    $"executors.strong '{policy.Executors.Strong}' is not defined in {TokonomyConfig.ExecutorsFileName}."));
            }

            if (!executors.Executors.ContainsKey(policy.Executors.Default))
            {
                issues.Add(new(Severity.Error, policyPath, null,
                    $"executors.default '{policy.Executors.Default}' is not defined in {TokonomyConfig.ExecutorsFileName}."));
            }
        }

        return new TokonomyConfig { ConfigDirectory = configDirectory, Policy = policy, Executors = executors, Issues = issues };
    }

    internal static T? Parse<T>(string path, List<ConfigIssue> issues)
        where T : class
    {
        if (!File.Exists(path))
        {
            issues.Add(new(Severity.Error, path, null, "file not found (run `tokonomy init`)."));
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

        if (policy.ArchitectProvider.Kind is not (ArchitectProviderConfig.ApiKind or ArchitectProviderConfig.ClaudeCliKind or ArchitectProviderConfig.AgyCliKind or ArchitectProviderConfig.CodexCliKind))
        {
            yield return new(Severity.Error, file, null,
                $"architect_provider.kind '{policy.ArchitectProvider.Kind}' is not supported; use '{ArchitectProviderConfig.ApiKind}', '{ArchitectProviderConfig.ClaudeCliKind}', '{ArchitectProviderConfig.AgyCliKind}' or '{ArchitectProviderConfig.CodexCliKind}'.");
        }

        if (!string.IsNullOrWhiteSpace(policy.ArchitectProvider.BaseUrl) &&
            (!Uri.TryCreate(policy.ArchitectProvider.BaseUrl, UriKind.Absolute, out var architectUri) || (architectUri.Scheme != "https" && architectUri.Scheme != "http")))
        {
            yield return new(Severity.Error, file, null, $"architect_provider.base_url '{policy.ArchitectProvider.BaseUrl}' is not an http(s) URL.");
        }

        if (policy.ArchitectProvider.ApiKeyEnv is { Length: > 0 } architectKey && LooksLikeSecret(architectKey))
        {
            yield return new(Severity.Error, file, null,
                "architect_provider.api_key_env looks like an API key. Put the *name* of an environment variable here, never the key.");
        }

        if (policy.Acceptance.Enabled)
        {
            if (Tokonomy.Core.Planning.AcceptancePaths.Normalize(policy.Acceptance.Dir, policy.Acceptance.Dir + "/x") is null)
            {
                yield return new(Severity.Error, file, null, "acceptance.dir must be a relative directory inside the repository (for example 'acceptance').");
            }

            if (policy.Acceptance.TimeoutSeconds <= 0)
            {
                yield return new(Severity.Error, file, null, "acceptance.timeout_seconds must be positive.");
            }
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

        if (!string.IsNullOrWhiteSpace(policy.Isolation.WorktreeRoot))
        {
            // policy.yaml lives in <repo>/.tokonomy/, so the repository is two levels up.
            var repo = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(file)));
            var root = Git.WorktreeRoot.Effective(policy.Isolation.WorktreeRoot, repo ?? Directory.GetCurrentDirectory());
            foreach (var problem in Git.WorktreeRoot.Validate(root, repo))
            {
                yield return new(Severity.Error, file, null, $"isolation.worktree_root: {problem}.");
            }
        }

        foreach (var issue in ValidateGateExecution(policy, file))
        {
            yield return issue;
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

        var p = policy.Planning;
        if (p.MaxRevisionRounds is < 0 or > 3)
        {
            yield return new(Severity.Error, file, null, "planning.max_revision_rounds must be between 0 and 3.");
        }

        if (p.MaxInspectFiles is < 0 or > 40)
        {
            yield return new(Severity.Error, file, null, "planning.max_inspect_files must be between 0 and 40.");
        }

        if (p.MaxContextChars < 5_000)
        {
            yield return new(Severity.Error, file, null, "planning.max_context_chars must be at least 5000.");
        }

        var s = policy.Supervision;
        foreach (var (key, value) in new[]
                 {
                     ("max_interventions_per_unit", s.MaxInterventionsPerUnit), ("max_interventions_per_run", s.MaxInterventionsPerRun),
                     ("max_splits_per_run", s.MaxSplitsPerRun), ("max_plan_revisions", s.MaxPlanRevisions),
                 }.Where(x => x.Item2 < 0))
        {
            yield return new(Severity.Error, file, null, $"supervision.{key} must be >= 0.");
        }

        var r = policy.Review;
        if (r.Mode is not (ReviewConfig.Off or ReviewConfig.Risk or ReviewConfig.Always))
        {
            yield return new(Severity.Error, file, null, $"review.mode '{r.Mode}' must be off, risk or always.");
        }

        if (r.ArchitectChanges is not (ReviewConfig.Off or ReviewConfig.Risk or ReviewConfig.Always))
        {
            yield return new(Severity.Error, file, null, $"review.architect_changes '{r.ArchitectChanges}' must be off, risk or always.");
        }

        if (r.OnUnavailable is not (ReviewConfig.OnUnavailableAccept or ReviewConfig.OnUnavailableFail))
        {
            yield return new(Severity.Error, file, null, $"review.on_unavailable '{r.OnUnavailable}' must be accept or fail.");
        }

        if (r.MaxChangeRequestsPerUnit < 0 || r.MediumRiskDiffLines <= 0)
        {
            yield return new(Severity.Error, file, null, "review.max_change_requests_per_unit must be >= 0 and review.medium_risk_diff_lines positive.");
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

    private static IEnumerable<ConfigIssue> ValidateGateExecution(Policy policy, string file)
    {
        var runsCommands = policy.Prepare.Count > 0 || policy.Acceptance.Enabled || policy.Gates.Any(g => g.Type == GateConfig.CommandType);
        if (policy.GateExecution is not { } g)
        {
            if (runsCommands)
            {
                yield return new(Severity.Warning, file, null,
                    "gate_execution is not set; `run` and `bench` refuse to execute gates and prepare steps until gate_execution.provider is windows-sandbox or host-unsafe.");
            }

            yield break;
        }

        switch (g.Provider)
        {
            case GateExecutionConfig.WindowsSandboxProvider:
                break;
            case GateExecutionConfig.HostUnsafeProvider:
                yield return new(Severity.Warning, file, null,
                    "gate_execution.provider is host-unsafe: gates run code the worker wrote on this machine with your privileges (development and trusted tasks only).");
                break;
            case "":
                yield return new(Severity.Error, file, null, "gate_execution.provider is required: windows-sandbox or host-unsafe.");
                break;
            default:
                yield return new(Severity.Error, file, null,
                    $"gate_execution.provider '{g.Provider}' is not supported; use '{GateExecutionConfig.WindowsSandboxProvider}' or '{GateExecutionConfig.HostUnsafeProvider}'.");
                break;
        }

        foreach (var (key, value) in new[] { ("network_policy", g.NetworkPolicy), ("prepare_network", g.PrepareNetwork) })
        {
            if (value is not (GateExecutionConfig.NetworkNone or GateExecutionConfig.NetworkFull))
            {
                yield return new(Severity.Error, file, null, $"gate_execution.{key} '{value}' must be 'none' or 'full'.");
            }
        }

        if (g.NetworkPolicy == GateExecutionConfig.NetworkFull)
        {
            yield return new(Severity.Warning, file, null,
                "gate_execution.network_policy is full: code under test can reach the internet, your LAN and services on this machine's network.");
        }

        if (g.CleanupPolicy is not (GateExecutionConfig.CleanupAlways or GateExecutionConfig.CleanupKeepOnFailure))
        {
            yield return new(Severity.Error, file, null, $"gate_execution.cleanup_policy '{g.CleanupPolicy}' must be 'always' or 'keep-on-failure'.");
        }

        var limits = new (string Key, int Value)[]
        {
            ("startup_timeout_seconds", g.StartupTimeoutSeconds),
            ("resource_limits.memory_mb", g.ResourceLimits.MemoryMb),
            ("artifact_limits.max_snapshot_mb", g.ArtifactLimits.MaxSnapshotMb),
            ("artifact_limits.max_dependencies_mb", g.ArtifactLimits.MaxDependenciesMb),
            ("artifact_limits.max_log_kb", g.ArtifactLimits.MaxLogKb),
            ("artifact_limits.max_report_mb", g.ArtifactLimits.MaxReportMb),
        };
        foreach (var (key, value) in limits.Where(l => l.Value <= 0))
        {
            yield return new(Severity.Error, file, null, $"gate_execution.{key} must be positive.");
        }

        if (g.ResourceLimits.MemoryMb is > 0 and < 1024)
        {
            yield return new(Severity.Warning, file, null, "gate_execution.resource_limits.memory_mb below 1024 is unlikely to boot Windows Sandbox.");
        }

        foreach (var toolchain in g.Toolchains.Where(t => !Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(t))))
        {
            yield return new(Severity.Error, file, null, $"gate_execution.toolchains: '{toolchain}' must be an absolute path.");
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
