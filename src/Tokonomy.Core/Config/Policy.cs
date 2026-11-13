namespace Tokonomy.Core.Config;

/// <summary>Contents of <c>.tokonomy/policy.yaml</c>.</summary>
public sealed class Policy
{
    public ModelsConfig Models { get; set; } = new();
    public ExecutorSelection Executors { get; set; } = new();
    public ProviderConfig Provider { get; set; } = new();
    public ArchitectProviderConfig ArchitectProvider { get; set; } = new();

    /// <summary>Names of every environment variable that holds a provider key (the worker's and the strong model's), to keep them out of worker and gate environments.</summary>
    public IReadOnlyList<string> KeyVariables =>
        new[] { Provider.ApiKeyEnv, ArchitectProvider.ApiKeyEnv ?? "" }.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    /// <summary>Commands run once per run before the baseline (dependency restore). Network is allowed.</summary>
    public List<GateConfig> Prepare { get; set; } = [];

    public List<GateConfig> Gates { get; set; } = [];

    /// <summary>Architect-written acceptance tests. Off unless <c>acceptance.cmd</c> is set.</summary>
    public AcceptanceConfig Acceptance { get; set; } = new();
    public EscalationConfig Escalation { get; set; } = new();

    /// <summary>How the architect plans: blueprint detail, self-review and targeted file inspection.</summary>
    public PlanningConfig Planning { get; set; } = new();

    /// <summary>Adaptive supervision (strategy 5): architect interventions, plan revisions, decomposition and recovery.</summary>
    public SupervisionConfig Supervision { get; set; } = new();

    /// <summary>Per-unit architectural review (strategy 5). Complements the gates; never overrides a failed gate.</summary>
    public ReviewConfig Review { get; set; } = new();

    public BudgetConfig Budget { get; set; } = new();
    public IsolationConfig Isolation { get; set; } = new();

    /// <summary>
    /// Where gates and prepare steps run. Null (section absent) is not a default: a run that has to execute a gate
    /// refuses to start until a provider is chosen, so code a worker wrote never runs on the host by accident.
    /// </summary>
    public GateExecutionConfig? GateExecution { get; set; }
}

/// <summary>
/// <c>gate_execution</c> in policy.yaml. Gate commands run code from the worktree (tests, build scripts, package lifecycle
/// scripts), which is code a worker wrote, so they run inside an isolated environment unless explicitly told otherwise.
/// </summary>
public sealed class GateExecutionConfig
{
    public const string WindowsSandboxProvider = "windows-sandbox";
    public const string HostUnsafeProvider = "host-unsafe";
    public const string NetworkNone = "none";
    public const string NetworkFull = "full";
    public const string CleanupAlways = "always";
    public const string CleanupKeepOnFailure = "keep-on-failure";

    /// <summary><c>windows-sandbox</c> or <c>host-unsafe</c> (development and tests only: gates run with your privileges).</summary>
    public string Provider { get; set; } = "";

    /// <summary>Network for gate commands: <c>none</c> (default) or <c>full</c>.</summary>
    public string NetworkPolicy { get; set; } = NetworkNone;

    /// <summary>Network for <c>prepare</c> steps, which run once on the base commit before any worker: <c>full</c> (default) or <c>none</c>.</summary>
    public string PrepareNetwork { get; set; } = NetworkFull;

    /// <summary>How long an environment may take to boot and accept commands.</summary>
    public int StartupTimeoutSeconds { get; set; } = 180;

    public GateResourceLimits ResourceLimits { get; set; } = new();
    public GateArtifactLimits ArtifactLimits { get; set; } = new();

    /// <summary><c>always</c> (default) or <c>keep-on-failure</c>: keep the host-side folders of a failed batch for inspection. The environment itself is always destroyed.</summary>
    public string CleanupPolicy { get; set; } = CleanupAlways;

    /// <summary>
    /// Host folders mapped read-only into the environment and put on its PATH (for example <c>C:/Program Files/dotnet</c>).
    /// Empty: derived from the gate and prepare commands. Folders under your profile, temp or the repository are refused.
    /// </summary>
    public List<string> Toolchains { get; set; } = [];
}

public sealed class GateResourceLimits
{
    public int MemoryMb { get; set; } = 4096;
}

public sealed class GateArtifactLimits
{
    /// <summary>Largest source snapshot sent into the environment.</summary>
    public int MaxSnapshotMb { get; set; } = 512;

    /// <summary>Largest dependency archive a prepare step may produce.</summary>
    public int MaxDependenciesMb { get; set; } = 4096;

    /// <summary>Largest gate log kept per command (the tail is kept).</summary>
    public int MaxLogKb { get; set; } = 1024;

    /// <summary>Largest test report collected per command.</summary>
    public int MaxReportMb { get; set; } = 16;
}

public sealed class IsolationConfig
{
    /// <summary>Extra environment variable names passed to workers on top of the built-in allowlist.</summary>
    public List<string> WorkerEnvAllow { get; set; } = [];

    /// <summary>Keep the run worktree on disk after the run (the run branch is always kept).</summary>
    public bool KeepWorktree { get; set; }

    /// <summary>
    /// Directory that holds run worktrees (default: the system temp directory). Set it outside temp for agents that treat
    /// temp as writable scratch space (Antigravity): otherwise a worker can write into every other run's worktree.
    /// A relative path is resolved against the repository root.
    /// </summary>
    public string? WorktreeRoot { get; set; }
}

public sealed class ModelsConfig
{
    public string Architect { get; set; } = "";
    public string Worker { get; set; } = "";
}

public sealed class ExecutorSelection
{
    /// <summary>The executor that runs the cheap model (<c>models.worker</c>).</summary>
    public string Default { get; set; } = "opencode";

    /// <summary>
    /// The executor that runs the strong model when it does the work itself (strategies 1 and 1b), for setups where the
    /// cheap model's agent cannot run it: Opus through Claude Code while the cheap model runs through Antigravity.
    /// Unset: the default executor.
    /// </summary>
    public string? Strong { get; set; }

    public string For(bool strongModelDoesTheWork) => strongModelDoesTheWork && !string.IsNullOrWhiteSpace(Strong) ? Strong : Default;
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
    public const string AgyCliKind = "agy-cli";
    public const string CodexCliKind = "codex-cli";

    /// <summary>
    /// <c>api</c> (default): an OpenAI-compatible endpoint with a key (this section's <c>base_url</c> and <c>api_key_env</c>
    /// when set, otherwise <c>provider</c>).
    /// <c>claude-cli</c>: run Claude Code headless on your own login (no API key); <c>models.architect</c> is passed to
    /// <c>claude --model</c> (for example <c>opus</c>). Its reported cost is an estimate.
    /// <c>agy-cli</c>: run the Antigravity CLI headless on your own login, with <c>models.architect</c> as its model.
    /// <c>codex-cli</c>: run the OpenAI Codex CLI headless on its own login (ChatGPT or an API key given to <c>codex login</c>), with <c>models.architect</c> as its model.
    /// </summary>
    public string Kind { get; set; } = ApiKind;

    /// <summary>Executable for <c>claude-cli</c>, <c>agy-cli</c> or <c>codex-cli</c>. Default: <c>claude</c> / <c>agy</c> / <c>codex</c>.</summary>
    public string Command { get; set; } = "";

    /// <summary>For <c>api</c>: the endpoint of the strong model when it differs from the worker's <c>provider</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>For <c>api</c>: the NAME of the environment variable with the strong model's key (never the key).</summary>
    public string? ApiKeyEnv { get; set; }

    public string CommandOrDefault => !string.IsNullOrWhiteSpace(Command) ? Command : Kind == AgyCliKind ? "agy" : Kind == CodexCliKind ? "codex" : "claude";

    /// <summary>The OpenAI-compatible endpoint architect calls use: this section's overrides on top of <paramref name="worker"/>.</summary>
    public ProviderConfig EffectiveProvider(ProviderConfig worker) => new()
    {
        BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? worker.BaseUrl : BaseUrl,
        ApiKeyEnv = string.IsNullOrWhiteSpace(ApiKeyEnv) ? worker.ApiKeyEnv : ApiKeyEnv,
    };
}

/// <summary>
/// <c>acceptance</c> in policy.yaml. The architect writes test files into <see cref="Dir"/> while planning; the worker
/// cannot change them (Tokonomy restores them before every gate run) and a unit is accepted only when <see cref="Cmd"/>
/// passes. Unlike <c>gates</c>, there is no baseline: a failure always blocks.
/// </summary>
public sealed class AcceptanceConfig
{
    /// <summary>Test runner command, e.g. <c>["python", "-m", "unittest", "discover", "-s", "acceptance"]</c>. Empty: feature off.</summary>
    public List<string> Cmd { get; set; } = [];

    /// <summary>Directory (relative to the repository root) the architect's test files must live in.</summary>
    public string Dir { get; set; } = "acceptance";

    /// <summary>One line telling the architect what kind of tests to write (language, framework, constraints).</summary>
    public string? Hint { get; set; }

    public int TimeoutSeconds { get; set; } = 300;

    public bool Enabled => Cmd.Count > 0 && !string.IsNullOrWhiteSpace(Cmd[0]);
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

    /// <summary>Declares that this gate needs network access. Enforced through <c>gate_execution.network_policy</c>, never per gate.</summary>
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

    /// <summary>
    /// Packages the user approves in advance, so adding them does not stop a unit for review: an exact name
    /// (<c>xunit</c>) or a prefix ending in <c>*</c> (<c>Microsoft.*</c>, <c>@types/*</c>). Names are compared ignoring case.
    /// A dependency change that cannot be tied to a package name (a lockfile, <c>go.mod</c>, ...) is never covered by this.
    /// </summary>
    public List<string> AllowedDependencies { get; set; } = [];
    public int WorkerTimeoutSeconds { get; set; } = 1200;

    /// <summary>
    /// After every unit passed, ask the architect once to review the combined diff. Advisory: it is recorded and shown,
    /// but never changes the run status (gates outrank opinions). Costs one architect call per run.
    /// </summary>
    public bool FinalReview { get; set; }
}

public sealed class BudgetConfig
{
    public decimal MaxUsdPerTask { get; set; } = 3.00m;
    public decimal? MaxUsdPerRun { get; set; }
    public double MaxArchitectTokenShare { get; set; } = 0.30;
}

/// <summary><c>planning</c> in policy.yaml. Applies to every strategy that uses an architect plan (3, 4, 5).</summary>
public sealed class PlanningConfig
{
    /// <summary>Ask for an engineering blueprint (analysis, architecture, decisions, detailed units). Off: the original short contract.</summary>
    public bool Blueprint { get; set; } = true;

    /// <summary>A second architect call critiques the plan before it is shown; a plan it rejects is revised.</summary>
    public bool Critique { get; set; } = true;

    /// <summary>Revisions after the deterministic quality check or the critique. 0 shows the first plan as is.</summary>
    public int MaxRevisionRounds { get; set; } = 1;

    /// <summary>The architect may ask once to read specific repository files (read-only, tracked files only) before it plans.</summary>
    public bool InspectFiles { get; set; } = true;

    public int MaxInspectFiles { get; set; } = 12;

    /// <summary>Upper bound on the repository context sent to the architect, in characters.</summary>
    public int MaxContextChars { get; set; } = 60_000;
}

/// <summary><c>supervision</c> in policy.yaml. Used by strategy 5 (adaptive supervision) only.</summary>
public sealed class SupervisionConfig
{
    /// <summary>Architect interventions (diagnosis plus correction) allowed per unit.</summary>
    public int MaxInterventionsPerUnit { get; set; } = 2;

    /// <summary>Architect interventions allowed per run, across all units.</summary>
    public int MaxInterventionsPerRun { get; set; } = 6;

    /// <summary>Call the architect as soon as the same gate failure repeats, instead of spending the remaining retries.</summary>
    public bool EarlyOnRepeatedFailure { get; set; } = true;

    /// <summary>After a failed takeover, reset the unit and let the worker resume with the architect's diagnosis (if interventions remain).</summary>
    public bool ResumeAfterFailedTakeover { get; set; } = true;

    /// <summary>Oversized units the architect may split into smaller ones per run. A unit made by a split is never split again.</summary>
    public int MaxSplitsPerRun { get; set; } = 2;

    /// <summary>Plan revisions allowed per run, a guard against revision loops.</summary>
    public int MaxPlanRevisions { get; set; } = 20;

    /// <summary>Read the worker's structured report (<c>tokonomy-report.json</c>) and act on blockers.</summary>
    public bool WorkerReports { get; set; } = true;
}

/// <summary><c>review</c> in policy.yaml: the architect reviews a unit's diff after its gates passed (strategy 5).</summary>
public sealed class ReviewConfig
{
    public const string Off = "off";
    public const string Risk = "risk";
    public const string Always = "always";
    public const string OnUnavailableAccept = "accept";
    public const string OnUnavailableFail = "fail";

    /// <summary><c>off</c>, <c>risk</c> (default: high-risk units and units with warning signs) or <c>always</c>.</summary>
    public string Mode { get; set; } = Risk;

    /// <summary>How many times a review may send a unit back to the worker before the unit escalates.</summary>
    public int MaxChangeRequestsPerUnit { get; set; } = 1;

    /// <summary>When the review cannot be obtained: <c>accept</c> (on the gates, with a warning) or <c>fail</c>.</summary>
    public string OnUnavailable { get; set; } = OnUnavailableAccept;

    /// <summary>In <c>risk</c> mode, a medium-risk unit is reviewed when its diff is at least this many lines.</summary>
    public int MediumRiskDiffLines { get; set; } = 150;

    /// <summary>
    /// Units the architect completed itself (L2, L3 or a takeover) after their gates passed: <c>risk</c> (default: only
    /// high-risk units, edits outside the plan, or large edits), <c>always</c> or <c>off</c>. The review is a separate call
    /// with a fresh context that is told it did not write the change.
    /// </summary>
    public string ArchitectChanges { get; set; } = Risk;

    /// <summary>Optional different model for reviewing architect-written changes (default: <c>models.architect</c>).</summary>
    public string? ArchitectReviewModel { get; set; }
}
