using System.Text;
using System.Text.Json;
using Taskrail.Core.Config;

namespace Taskrail.Runtime.Config;

public sealed record ScaffoldResult(string ConfigDirectory, IReadOnlyList<StackInfo> Stacks, IReadOnlyList<string> Written, IReadOnlyList<string> Skipped);

/// <summary>Writes the initial <c>.taskrail/</c> directory. Existing files are kept unless <c>force</c> is set.</summary>
public static class Scaffolder
{
    public static ScaffoldResult Init(string repoRoot, bool force)
    {
        var dir = Path.Combine(repoRoot, TaskrailConfig.DirectoryName);
        Directory.CreateDirectory(dir);

        var stacks = StackDetector.Detect(repoRoot);
        var files = new (string Name, string Content)[]
        {
            (TaskrailConfig.PolicyFile, PolicyYaml(stacks)),
            (TaskrailConfig.ExecutorsFileName, ExecutorsYaml),
            (TaskrailConfig.PricesFile, PricesYaml),
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
        sb.AppendLine("# Taskrail policy. Docs: docs/config.md");
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
        # `taskrail doctor` compares it with what is installed.
        #
        # Placeholders in cmd/env/files: {prompt} {model} {workdir} {proxy_url} (ends in /v1) {proxy_root} {proxy_token}.
        # Workers get a scrubbed environment: no provider API keys, only the per-run proxy token.
        # `files` are written into the worktree for one attempt and restored afterwards (never committed).

        executors:
          opencode:
            # Per opencode.ai/docs (V2): prompt is positional; project config in opencode.json supports {env:VAR}.
            # UNVERIFIED: `run` flags are not documented. Check `opencode run --help` for the model flag and
            # confirm the provider/permissions shape below, then set tested_version.
            cmd: ["opencode", "run", "{prompt}"]
            model_args: ["--model", "taskrail/{model}"]
            brief_file: AGENTS.md
            version_cmd: ["opencode", "--version"]
            tested_version: null
            env:
              TASKRAIL_PROXY_TOKEN: "{proxy_token}"
            files:
              opencode.json: |
                {
                  "$schema": "https://opencode.ai/config.json",
                  "providers": {
                    "taskrail": {
                      "name": "Taskrail metering proxy",
                      "env": ["TASKRAIL_PROXY_TOKEN"],
                      "package": "@opencode/ai/providers/openai-compatible",
                      "settings": { "baseURL": "{proxy_url}", "apiKey": "{env:TASKRAIL_PROXY_TOKEN}" },
                      "models": { "{model}": { "name": "{model}" } }
                    }
                  },
                  "permissions": [
                    { "action": "edit", "resource": "*", "effect": "allow" },
                    { "action": "shell", "resource": "*", "effect": "allow" },
                    { "action": "external_directory", "resource": "*", "effect": "deny" }
                  ]
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
        """;

    internal const string PricesYaml = """
        # Model prices in USD per million tokens, used to cost architect calls and metered worker calls.
        # Taskrail ships no built-in prices: they change often. Fill in the models you use from your
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
        # Taskrail local state. Commit policy.yaml, executors.yaml and prices.yaml; ignore the rest.
        runs/
        tmp/
        ledger.db
        ledger.db-*
        """;
}
