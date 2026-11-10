namespace Tokonomy.Core.Config;

/// <summary>Contents of <c>.tokonomy/executors.yaml</c>.</summary>
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

    /// <summary>
    /// How to read usage from the agent's stdout when its traffic is not metered by the proxy. Supported:
    /// <c>claude-json</c> (Claude Code <c>--output-format json</c>) and <c>agy-json</c> (Antigravity CLI <c>--output-format
    /// stream-json</c> or <c>json</c>: tokens, selected model, shell commands run and auto-denied tool calls).
    /// Reported cost is recorded as estimated,
    /// because it is the agent's own API-equivalent figure (a subscription login is not billed per token).
    /// </summary>
    public string? UsageFormat { get; set; }

    /// <summary>
    /// Permission rules for agents that take them in a settings file (Antigravity <c>permissions.allow</c>/<c>deny</c>).
    /// Tokonomy writes them to a per-attempt file outside the worktree and exposes its path as <c>{settings_file}</c>.
    /// </summary>
    public ExecutorPermissions Permissions { get; set; } = new();

    /// <summary>
    /// Fail the attempt when the agent reports a different model than the one requested (no silent fallback).
    /// Only effective for usage formats that report the model (<c>agy-json</c>).
    /// </summary>
    public bool VerifyModel { get; set; } = true;

    public ExecutorCapabilities Capabilities { get; set; } = new();
}

/// <summary>
/// Least-privilege rules for a headless coding agent. Antigravity 1.5.3 matches <c>command(...)</c> rules literally,
/// token by token and for the whole command line: there are no prefix or wildcard rules, so every entry is one exact command.
/// </summary>
public sealed class ExecutorPermissions
{
    /// <summary>Exact extra commands the worker may run, e.g. <c>python -m unittest</c>. Written as <c>command(...)</c> rules.</summary>
    public List<string> Commands { get; set; } = [];

    /// <summary>
    /// Also allow each policy gate's exact command line. Off by default: a test command executes code the worker wrote
    /// (its tests, or a module that shadows the standard library), so granting it is equivalent to arbitrary code
    /// execution inside the agent session, which can read credential files and plant links mid-run. The gates still
    /// run after every attempt, outside the agent.
    /// </summary>
    public bool AllowGateCommands { get; set; }

    /// <summary>Raw rules added verbatim to <c>permissions.allow</c> (for example <c>read_url(docs.python.org)</c>).</summary>
    public List<string> Allow { get; set; } = [];

    /// <summary>Raw rules added verbatim to <c>permissions.deny</c>. Deny always wins over allow.</summary>
    public List<string> Deny { get; set; } = [];

    /// <summary>Grant <c>read_file</c>/<c>write_file</c> on the worktree only (and nothing else) instead of an edit mode.</summary>
    public bool ScopeFilesToWorktree { get; set; } = true;

    /// <summary>
    /// Deny <c>write_file</c> on the system temp directory when the worktree is outside it. The agent treats temp as
    /// scratch space and lets it be written without any rule, which would include other runs' worktrees.
    /// </summary>
    public bool DenyTempWrites { get; set; } = true;

    /// <summary>Same for <c>read_file</c>: temp is readable by any run, including other runs' worktrees. Applies together with the write deny.</summary>
    public bool DenyTempReads { get; set; } = true;
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

    /// <summary>Whether the executor's LLM traffic can be redirected to Tokonomy's metering proxy.</summary>
    public bool ProxyRoutable { get; set; }
}
