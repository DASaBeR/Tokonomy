using System.Text;
using System.Text.Json;
using Tokonomy.Core.Config;

namespace Tokonomy.Runtime.Config;

public sealed record ScaffoldResult(string ConfigDirectory, IReadOnlyList<StackInfo> Stacks, IReadOnlyList<string> Written, IReadOnlyList<string> Skipped);

/// <summary>Writes the initial <c>.tokonomy/</c> directory. Existing files are kept unless <c>force</c> is set.</summary>
public static class Scaffolder
{
    public static ScaffoldResult Init(string repoRoot, bool force)
    {
        var dir = Path.Combine(repoRoot, TokonomyConfig.DirectoryName);
        Directory.CreateDirectory(dir);

        var stacks = StackDetector.Detect(repoRoot);
        var files = new (string Name, string Content)[]
        {
            (TokonomyConfig.PolicyFile, PolicyYaml(stacks)),
            (TokonomyConfig.ExecutorsFileName, ExecutorsYaml),
            (TokonomyConfig.PricesFile, PricesYaml),
            (".gitignore", GitIgnore),
        };

        var written = new List<string>();
        var skipped = new List<string>();
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && !force)
            {
                skipped.Add(name);
                continue;
            }

            File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(false));
            written.Add(name);
        }

        return new ScaffoldResult(dir, stacks, written, skipped);
    }

    internal static string PolicyYaml(IReadOnlyList<StackInfo> stacks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Tokonomy policy. Docs: docs/config.md");
        sb.AppendLine("# LLMs produce content; this file makes decisions.");
        sb.AppendLine();
        sb.AppendLine("models:");
        sb.AppendLine("  architect: \"\"   # strong model, e.g. an OpenRouter model id");
        sb.AppendLine("  worker: \"\"      # cheap model passed to the executor as a model override");
        sb.AppendLine();
        sb.AppendLine("provider:");
        sb.AppendLine("  base_url: https://openrouter.ai/api/v1");
        sb.AppendLine("  api_key_env: OPENROUTER_API_KEY   # the variable NAME; the key never goes in this file");
        sb.AppendLine();
        sb.AppendLine("executors:");
        sb.AppendLine("  default: opencode");
        sb.AppendLine();
        sb.AppendLine("# Run once per run in the fresh worktree, before the baseline (network allowed).");
        var prepare = stacks.SelectMany(s => s.Prepare).ToList();
        if (prepare.Count == 0)
        {
            sb.AppendLine("prepare: []   # e.g. [{ name: deps, cmd: [\"pip\", \"install\", \"-e\", \".\"] }]");
        }
        else
        {
            sb.AppendLine("prepare:");
            foreach (var step in prepare)
            {
                sb.AppendLine("  - " + InlineGate(step));
            }
        }

        sb.AppendLine();
        sb.AppendLine("# Gates are argument arrays, never shell strings. They run on the base commit first;");
        sb.AppendLine("# only failures that are new relative to that baseline block a unit.");
        sb.AppendLine("gates:");
        if (stacks.Count == 0)
        {
            sb.AppendLine("  # No stack detected. Add your build/lint/test commands, e.g.:");
            sb.AppendLine("  # - { name: test, cmd: [\"make\", \"test\"] }");
        }

        foreach (var stack in stacks)
        {
            sb.AppendLine($"  # detected: {stack.Name} ({stack.Marker})");
            foreach (var gate in stack.Gates)
            {
                sb.AppendLine("  - " + InlineGate(gate));
            }
        }

        sb.AppendLine("  - { type: locked_regions }");
        sb.AppendLine();
        sb.AppendLine("escalation:");
        sb.AppendLine("  max_retries_per_unit: 2    # failed attempts on a unit before escalation");
        sb.AppendLine("  max_takeovers_per_unit: 1  # architect takeovers allowed per unit (0 disables takeover)");
        sb.AppendLine("  protected_paths: []        # e.g. [\"src/auth/**\", \"migrations/**\"]");
        sb.AppendLine("  max_diff_lines: 400        # larger diffs are rejected and the unit is split");
        sb.AppendLine("  review_on_new_dependency: true");
        sb.AppendLine("  worker_timeout_seconds: 1200");
        sb.AppendLine();
        sb.AppendLine("budget:");
        sb.AppendLine("  max_usd_per_task: 3.00     # hard abort");
        sb.AppendLine("  max_architect_token_share: 0.30");
        sb.AppendLine();
        sb.AppendLine("isolation:");
        sb.AppendLine("  worker_env_allow: []       # extra env var names workers may see (keys are never passed)");
        sb.AppendLine("  keep_worktree: false");
        sb.AppendLine("  # worktree_root: \"D:/tokonomy-worktrees\"   # default: %LOCALAPPDATA%/tokonomy/worktrees; must not overlap temp, the repo or home");
        sb.AppendLine();
        sb.AppendLine("# Gates and prepare steps run code the worker wrote. They run in a disposable Windows Sandbox VM:");
        sb.AppendLine("# a copy of the source (no .git, no .tokonomy), no network for gates, host toolchains mapped read-only.");
        sb.AppendLine("# Needs Windows Pro/Enterprise/Education 24H2+ with the Windows Sandbox feature (see docs/config.md).");
        sb.AppendLine("# host-unsafe runs them on this machine with your privileges; there is no automatic fallback.");
        sb.AppendLine("gate_execution:");
        sb.AppendLine("  provider: windows-sandbox     # windows-sandbox | host-unsafe");
        sb.AppendLine("  network_policy: none          # gates: none | full");
        sb.AppendLine("  prepare_network: full         # prepare steps (base commit only): none | full");
        sb.AppendLine("  resource_limits: { memory_mb: 4096 }");
        sb.AppendLine("  toolchains: []                # default: the folders of the gate commands' executables, e.g. [\"C:/Program Files/dotnet\"]");
        return sb.ToString();
    }

    private static string InlineGate(GateConfig gate)
    {
        var parts = new List<string>();
        if (gate.Name is not null)
        {
            parts.Add($"name: {gate.Name}");
        }

        parts.Add($"cmd: [{string.Join(", ", gate.Cmd.Select(Quote))}]");
        if (gate.Report is not null)
        {
            parts.Add($"report: {Quote(gate.Report)}");
        }

        return "{ " + string.Join(", ", parts) + " }";
    }

    // JSON string literals are valid YAML double-quoted scalars.
    private static string Quote(string s) => JsonSerializer.Serialize(s);

    internal const string ExecutorsYaml = """
        # Executor adapters: config-driven wrappers around coding-agent CLIs.
        # Agent CLI flags change often. Each entry records the version it was tested against;
        # `tokonomy doctor` compares it with what is installed.
        #
        # Placeholders in cmd/env/files: {prompt} {model} {workdir} {proxy_url} (ends in /v1) {proxy_root} {proxy_token}.
        # Workers get a scrubbed environment: no provider API keys, only the per-run proxy token.
        # `files` are written into the worktree for one attempt and restored afterwards (never committed).

        executors:
          opencode:
            # Per opencode.ai/docs (V2): prompt is positional; project config in opencode.json supports {env:VAR}.
            # Verified against OpenCode 1.19.5 (the `opencode-ai` npm package, "V1"): `run -m provider/model`,
            # V1 config shape (`provider`/`permission`). The V2 docs shape is rejected by this version.
            cmd: ["opencode", "run", "{prompt}"]
            model_args: ["--model", "tokonomy/{model}"]
            brief_file: AGENTS.md
            version_cmd: ["opencode", "--version"]
            tested_version: "1.19.5"
            env:
              TOKONOMY_PROXY_TOKEN: "{proxy_token}"
            files:
              opencode.json: |
                {
                  "$schema": "https://opencode.ai/config.json",
                  "autoupdate": false,
                  "share": "disabled",
                  "provider": {
                    "tokonomy": {
                      "npm": "@ai-sdk/openai-compatible",
                      "name": "Tokonomy metering proxy",
                      "options": { "baseURL": "{proxy_url}", "apiKey": "{env:TOKONOMY_PROXY_TOKEN}" },
                      "models": { "{model}": { "name": "{model}" } }
                    }
                  },
                  "permission": { "edit": "allow", "bash": "allow", "webfetch": "deny", "external_directory": "deny" }
                }
            capabilities:
              headless: true
              model_override: true
              structured_output: false
              session_resume: false
              reports_tokens: false
              permission_modes: true
              proxy_routable: true

          claude-code:
            # UNVERIFIED: confirm flags against current Claude Code docs before first use (M3).
            cmd: ["claude", "-p", "{prompt}", "--output-format", "json", "--permission-mode", "acceptEdits"]
            model_args: ["--model", "{model}"]
            brief_file: CLAUDE.md
            version_cmd: ["claude", "--version"]
            tested_version: null
            env:
              ANTHROPIC_BASE_URL: "{proxy_root}"
              ANTHROPIC_AUTH_TOKEN: "{proxy_token}"
            capabilities:
              headless: true
              model_override: true
              structured_output: true
              session_resume: true
              reports_tokens: true
              permission_modes: true
              proxy_routable: true

          claude-code-sub:
            # Claude Code on a subscription login (no API key). No proxy and no endpoint override, so the login keeps
            # working; usage and cost come from Claude Code's own JSON output and are recorded as estimated.
            # Check your plan's terms before running automated batches. Smoke-tested against Claude Code 2.1.316.
            cmd: ["claude", "-p", "{prompt}", "--output-format", "json", "--permission-mode", "acceptEdits", "--no-session-persistence"]
            model_args: ["--model", "{model}"]
            brief_file: CLAUDE.md
            version_cmd: ["claude", "--version"]
            tested_version: "2.1.316"
            usage_format: claude-json
            capabilities:
              headless: true
              model_override: true
              structured_output: true
              session_resume: false
              reports_tokens: true
              permission_modes: true
              proxy_routable: false

          antigravity:
            # Antigravity CLI (`agy`) on your own Antigravity login: no API key, no proxy. Run `agy models` for ids such as
            # gemini-3.8-flash-medium. Tokens come from its JSON output; cost is unknown and recorded as estimated.
            # `agy` is found on PATH, then in ~/.gemini/bin (where the installer puts it on Windows).
            #
            # Headless mode cannot prompt, so permissions are given up front and never with --dangerously-skip-permissions:
            #  * No edit mode. `permissions` below becomes a per-attempt --settings file ({settings_file}, written outside the
            #    worktree): read_file/write_file on the worktree only, plus one literal command(...) rule per allowed command.
            #    agy 1.5.3 matches command rules literally (no prefix, no wildcards, regex characters are NOT interpreted).
            #  * stream-json lets Tokonomy see the model the session really used, every shell command and every refused call.
            #    The run is failed (never accepted) on a model mismatch, a denied or out-of-allowlist command, a non-zero exit
            #    or an error status. Nothing falls back to another model.
            #  * Worktrees live outside temp by default (agy lets any run read and write temp); temp is denied too.
            #  * No shell commands by default: a test command runs code the worker wrote (docs/threat-model.md, T16).
            cmd: ["agy", "-p", "{prompt}", "--output-format", "stream-json", "--settings", "{settings_file}", "--sandbox"]
            model_args: ["--model", "{model}"]
            brief_file: AGENTS.md
            version_cmd: ["agy", "--version"]
            tested_version: "1.5.3"
            usage_format: agy-json
            verify_model: true
            permissions:
              commands: []                # extra exact command lines, e.g. ["python -m unittest"]
              allow_gate_commands: false  # true lets the worker run the gates itself, which runs code it wrote: see threat-model T16
              scope_files_to_worktree: true
              deny_temp_writes: true      # only effective when the worktree is outside temp
              deny_temp_reads: true       # same: agy lets any run read and write temp unless denied
              allow: []                   # raw extra rules, e.g. "read_url(docs.python.org)"
              deny: []                    # raw extra rules; deny always beats allow
            capabilities:
              headless: true
              model_override: true
              structured_output: true
              session_resume: false
              reports_tokens: true
              permission_modes: true
              proxy_routable: false
        """;

    internal const string PricesYaml = """
        # Model prices in USD per million tokens, used to cost architect calls and metered worker calls.
        # Tokonomy ships no built-in prices: they change often. Fill in the models you use from your
        # provider's pricing page. Calls to models missing here are recorded with cost "estimated".
        #
        # models:
        #   "provider/model-id":
        #     input: 0.00
        #     output: 0.00
        #     cached_input: 0.00

        models: {}
        """;

    internal const string GitIgnore = """
        # Tokonomy local state. Commit policy.yaml, executors.yaml and prices.yaml; ignore the rest.
        runs/
        tmp/
        ledger.db
        ledger.db-*
        """;
}
