namespace Tokonomy.Core.Config;

/// <summary>Contents of <c>.tokonomy/policy.yaml</c>.</summary>
public sealed class Policy
{
    public ModelsConfig Models { get; set; } = new();
    public ExecutorSelection Executors { get; set; } = new();
    public ProviderConfig Provider { get; set; } = new();
    public ArchitectProviderConfig ArchitectProvider { get; set; } = new();
    /// <summary>Commands run once per run before the baseline (dependency restore). Network is allowed.</summary>
    public List<GateConfig> Prepare { get; set; } = [];

    public List<GateConfig> Gates { get; set; } = [];
    public EscalationConfig Escalation { get; set; } = new();
    public BudgetConfig Budget { get; set; } = new();
    public IsolationConfig Isolation { get; set; } = new();
}

public sealed class IsolationConfig
{
    /// <summary>Extra environment variable names passed to workers on top of the built-in allowlist.</summary>
    public List<string> WorkerEnvAllow { get; set; } = [];

    /// <summary>Keep the run worktree on disk after the run (the run branch is always kept).</summary>
    public bool KeepWorktree { get; set; }
}

public sealed class ModelsConfig
{
    public string Architect { get; set; } = "";
    public string Worker { get; set; } = "";
}

public sealed class ExecutorSelection
{
    public string Default { get; set; } = "opencode";
}

public sealed class ProviderConfig
{
    /// <summary>OpenAI-compatible base URL. OpenRouter is the default universal route.</summary>
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>Name of the environment variable holding the API key. The key itself never lives in config.</summary>
    public string ApiKeyEnv { get; set; } = "OPENROUTER_API_KEY";
}

/// <summary>How architect calls (planning, takeover) are made. Worker traffic always uses <see cref="ProviderConfig"/>.</summary>
public sealed class ArchitectProviderConfig
{
    public const string ApiKind = "api";
    public const string ClaudeCliKind = "claude-cli";

    /// <summary>
    /// <c>api</c> (default): an OpenAI-compatible endpoint with a key, see <c>provider</c>.
    /// <c>claude-cli</c>: run Claude Code headless on your own login (no API key); <c>models.architect</c> is passed to
    /// <c>claude --model</c> (for example <c>opus</c>). Its reported cost is an estimate.
    /// </summary>
    public string Kind { get; set; } = ApiKind;

    /// <summary>Executable for <c>claude-cli</c>.</summary>
    public string Command { get; set; } = "claude";
}

public sealed class GateConfig
{
    public const string CommandType = "command";
    public const string LockedRegionsType = "locked_regions";

    /// <summary>Gate kind: <c>command</c> (default) or <c>locked_regions</c>.</summary>
    public string Type { get; set; } = CommandType;

    public string? Name { get; set; }

    /// <summary>Argument array. Never a shell string.</summary>
    public List<string> Cmd { get; set; } = [];

    /// <summary>Optional test report path (TRX or JUnit XML) used for per-test baselining.</summary>
    public string? Report { get; set; }

    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>Declares that this gate needs network access.</summary>
    public bool Network { get; set; }

    public string DisplayName => Name ?? (Cmd.Count > 0 ? string.Join(' ', Cmd) : Type);
}

public sealed class EscalationConfig
{
    public int MaxRetriesPerUnit { get; set; } = 2;

    /// <summary>Architect takeovers allowed per unit. 0 disables takeover (escalations fail the unit).</summary>
    public int MaxTakeoversPerUnit { get; set; } = 1;
    public List<string> ProtectedPaths { get; set; } = [];
    public int MaxDiffLines { get; set; } = 400;
    public bool ReviewOnNewDependency { get; set; } = true;
    public int WorkerTimeoutSeconds { get; set; } = 1200;
}

public sealed class BudgetConfig
{
    public decimal MaxUsdPerTask { get; set; } = 3.00m;
    public decimal? MaxUsdPerRun { get; set; }
    public double MaxArchitectTokenShare { get; set; } = 0.30;
}
