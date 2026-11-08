namespace Taskrail.Core.Config;

/// <summary>Contents of <c>.taskrail/executors.yaml</c>.</summary>
public sealed class ExecutorsFile
{
    public Dictionary<string, ExecutorDefinition> Executors { get; set; } = [];
}

/// <summary>A config-driven adapter around a coding-agent CLI.</summary>
public sealed class ExecutorDefinition
{
    /// <summary>Argument array; supports <c>{prompt}</c>, <c>{model}</c> and <c>{workdir}</c> placeholders.</summary>
    public List<string> Cmd { get; set; } = [];

    /// <summary>Extra arguments appended only when a model override is given.</summary>
    public List<string> ModelArgs { get; set; } = [];

    /// <summary>The agent's native instructions file (e.g. AGENTS.md, CLAUDE.md, GEMINI.md).</summary>
    public string BriefFile { get; set; } = "AGENTS.md";

    /// <summary>Command that prints the executor version, used by <c>doctor</c>.</summary>
    public List<string> VersionCmd { get; set; } = [];

    /// <summary>Version this adapter was tested against.</summary>
    public string? TestedVersion { get; set; }

    /// <summary>
    /// Environment variables passed to the worker. Values may use <c>{proxy_url}</c> (OpenAI-style base, ends in /v1),
    /// <c>{proxy_root}</c> (bare origin), <c>{proxy_token}</c> and <c>{model}</c>.
    /// </summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>
    /// Files written into the worktree for the duration of one attempt (e.g. an agent config that routes traffic
    /// through the metering proxy). Same placeholders as <see cref="Env"/>. Existing files are restored afterwards.
    /// </summary>
    public Dictionary<string, string> Files { get; set; } = [];

    public ExecutorCapabilities Capabilities { get; set; } = new();
}

public sealed class ExecutorCapabilities
{
    public bool Headless { get; set; }
    public bool ModelOverride { get; set; }
    public bool StructuredOutput { get; set; }
    public bool SessionResume { get; set; }

    /// <summary>When false and the executor is not proxy-routable, the ledger marks its cost as estimated.</summary>
    public bool ReportsTokens { get; set; }

    public bool PermissionModes { get; set; }

    /// <summary>Whether the executor's LLM traffic can be redirected to Taskrail's metering proxy.</summary>
    public bool ProxyRoutable { get; set; }
}
