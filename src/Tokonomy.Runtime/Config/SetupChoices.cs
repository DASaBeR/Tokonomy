using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Config;

/// <summary>An OpenAI-compatible HTTP API the user can reach with a key.</summary>
public sealed record ApiService(string Id, string Title, string BaseUrl, string KeyVariable, string KeyUrl, bool Verified)
{
    /// <summary>True when Tokonomy has run this service end to end (planning and metered coding); the others follow the same protocol but are untested.</summary>
    public string Tested => Verified ? "" : " (not tested live)";
}

public enum ConnectionKind
{
    /// <summary>Claude Code, on your Claude subscription (no key).</summary>
    ClaudeCode,

    /// <summary>The Antigravity CLI (<c>agy</c>), on your Antigravity login (it has no API-key option).</summary>
    Antigravity,

    /// <summary>An OpenAI-compatible API with a key in an environment variable.</summary>
    Api,

    /// <summary>The OpenAI Codex CLI, on its own login (a ChatGPT plan, or an API key given to <c>codex login</c>).</summary>
    Codex,
}

/// <summary>How one role (strong or cheap) reaches its model.</summary>
public sealed record Connection(ConnectionKind Kind, ApiService? Api = null)
{
    public static Connection Claude { get; } = new(ConnectionKind.ClaudeCode);
    public static Connection Antigravity { get; } = new(ConnectionKind.Antigravity);
    public static Connection Codex { get; } = new(ConnectionKind.Codex);
    public static Connection For(ApiService service) => new(ConnectionKind.Api, service);

    public string Title => Kind switch
    {
        ConnectionKind.ClaudeCode => "Claude Code subscription",
        ConnectionKind.Antigravity => "Antigravity subscription",
        ConnectionKind.Codex => "Codex CLI (ChatGPT plan or API key)",
        _ => $"{Api!.Title} API key",
    };

    public bool IsApi => Kind == ConnectionKind.Api;
}

/// <summary>Everything the setup wizard (or its flags) decides. <see cref="Scaffolder"/> turns it into config files.</summary>
public sealed record SetupChoices
{
    /// <summary>Connection for the strong model: plans, takes over hard units, reviews, writes the tests.</summary>
    public required Connection Strong { get; init; }

    /// <summary>Connection for the cheap model that writes the code.</summary>
    public required Connection Cheap { get; init; }

    public required string ArchitectModel { get; init; }
    public required string WorkerModel { get; init; }

    /// <summary>When set, the architect writes the acceptance tests and this command runs them.</summary>
    public AcceptanceSetup? Acceptance { get; init; }

    /// <summary>USD per million tokens (input, output) for chosen models whose price is known, written to prices.yaml.</summary>
    public IReadOnlyDictionary<string, (decimal Input, decimal Output)> Prices { get; init; } = new Dictionary<string, (decimal, decimal)>();

    /// <summary><c>windows-sandbox</c> or <c>host-unsafe</c>: where the tests and builds the worker's code runs.</summary>
    public string GateExecution { get; init; } = "windows-sandbox";

    /// <summary>Environment variables that must hold a key, from the API connections in use.</summary>
    public IReadOnlyList<ApiService> ApiServices =>
        new[] { Strong.Api, Cheap.Api }.OfType<ApiService>().DistinctBy(s => s.KeyVariable, StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record AcceptanceSetup(IReadOnlyList<string> Cmd, string Hint);

/// <summary>What each connection offers, needs and is called. The menus, the flags and the config all read from here.</summary>
public static class Sources
{
    public static ApiService OpenRouter { get; } = new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY", "https://openrouter.ai/keys", Verified: true);
    public static ApiService Anthropic { get; } = new("anthropic", "Anthropic", "https://api.anthropic.com/v1", "ANTHROPIC_API_KEY", "https://console.anthropic.com/settings/keys", Verified: false);
    public static ApiService Gemini { get; } = new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "GEMINI_API_KEY", "https://aistudio.google.com/apikey", Verified: false);
    public static ApiService OpenAi { get; } = new("openai", "OpenAI", "https://api.openai.com/v1", "OPENAI_API_KEY", "https://platform.openai.com/api-keys", Verified: false);
    public static ApiService DeepSeek { get; } = new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "DEEPSEEK_API_KEY", "https://platform.deepseek.com/api_keys", Verified: false);

    public static IReadOnlyList<ApiService> ApiServices { get; } = [OpenRouter, Anthropic, Gemini, OpenAi, DeepSeek];

    /// <summary>Id of a provider the user typed in (AvalAI, a gateway, a local server): any OpenAI-compatible endpoint.</summary>
    public const string CustomId = "custom";

    public static ApiService Custom(string name, string baseUrl, string keyVariable) =>
        new(CustomId, name, baseUrl.TrimEnd('/'), keyVariable, "", Verified: false);

    /// <summary>Claude Code cannot list the models of a plan, so these are the ones it is known to accept; each choice is tested with a tiny call.</summary>
    public static IReadOnlyList<(string Id, string Note)> ClaudeModels { get; } =
    [
        ("claude-fable-5-1", "most capable"),
        ("claude-opus-5-5", "strong, good for planning"),
        ("claude-sonnet-5-5", "balanced"),
        ("claude-haiku-5-5", "fastest and cheapest"),
    ];

    /// <summary>The executor entry (in executors.yaml) that runs the cheap model.</summary>
    public static string Executor(Connection cheap) => cheap.Kind switch
    {
        ConnectionKind.ClaudeCode => "claude-code-sub",
        ConnectionKind.Antigravity => "antigravity",
        ConnectionKind.Codex => "codex",
        _ => "opencode",
    };

    /// <summary>Command-line tools the connection needs. The cheap role writes files, so it always needs an agent CLI; the strong role only talks.</summary>
    public static IReadOnlyList<string> Tools(Connection connection, bool strongRole) => connection.Kind switch
    {
        ConnectionKind.ClaudeCode => ["claude"],
        ConnectionKind.Antigravity => ["agy"],
        ConnectionKind.Codex => ["codex"],
        _ => strongRole ? [] : ["opencode"],
    };

    /// <summary>
    /// Codex cannot list the models of a plan. These are common ids; which ones work depends on your plan, so each choice is
    /// tested with a tiny call and any other id can be typed.
    /// </summary>
    public static IReadOnlyList<(string Id, string Note)> CodexModels { get; } =
    [
        ("gpt-5-codex", "tuned for coding"),
        ("gpt-5", "general, strongest reasoning"),
        ("gpt-5-mini", "faster and cheaper"),
    ];

    public static string DefaultStrong(Connection connection) => connection.Kind switch
    {
        ConnectionKind.ClaudeCode => "claude-opus-5-5",
        ConnectionKind.Codex => "gpt-5-codex",
        _ => "",
    };

    public static string DefaultCheap(Connection connection) => connection.Kind switch
    {
        ConnectionKind.ClaudeCode => "claude-haiku-5-5",
        ConnectionKind.Antigravity => "gemini-3.8-flash-medium",
        ConnectionKind.Codex => "gpt-5-mini",
        _ => "",
    };

    /// <summary>Flag values: <c>claude</c>, <c>antigravity</c>, <c>codex</c>, <c>openrouter</c>, <c>anthropic</c>, <c>gemini</c>, <c>openai</c>, <c>deepseek</c>.</summary>
    public static bool TryParse(string? text, out Connection connection)
    {
        connection = Connection.Claude;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "claude" or "claude-code":
                return true;
            case "antigravity" or "agy":
                connection = Connection.Antigravity;
                return true;
            case "codex" or "chatgpt" or "gpt":
                connection = Connection.Codex;
                return true;
            case { } id when ApiServices.FirstOrDefault(s => s.Id == id || (id == "api" && s.Id == "openrouter")) is { } service:
                connection = Connection.For(service);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Shortcuts for common pairs, kept so one flag still sets both roles.</summary>
    public static bool TryPreset(string? preset, out Connection strong, out Connection cheap)
    {
        (strong, cheap) = (Connection.Claude, Connection.Claude);
        switch (preset?.ToLowerInvariant())
        {
            case "claude-antigravity":
                cheap = Connection.Antigravity;
                return true;
            case "claude":
                return true;
            case "api":
                (strong, cheap) = (Connection.For(OpenRouter), Connection.For(OpenRouter));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A test runner for architect-written acceptance tests, using whatever is installed: Python's unittest first, then
    /// Node's built-in runner. Null when neither is found.
    /// </summary>
    public static AcceptanceSetup? DetectAcceptance(string repoRoot)
    {
        if (ExecutableResolver.TryResolve("python", repoRoot) is not null)
        {
            return new AcceptanceSetup(
                ["python", "-m", "unittest", "discover", "-s", "acceptance"],
                "Write tests with the Python standard library only (unittest, pathlib, re, html.parser, json). " +
                "The project root is the parent of acceptance/: locate files with pathlib relative to __file__. Name files test_*.py.");
        }

        if (ExecutableResolver.TryResolve("node", repoRoot) is not null)
        {
            return new AcceptanceSetup(
                ["node", "--test", "acceptance/"],
                "Write tests with Node's built-in node:test and node:assert only; no npm packages. " +
                "Locate project files relative to the process working directory (the project root). Name files *.test.js.");
        }

        return null;
    }
}
