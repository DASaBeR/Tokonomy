# Configuration

`taskrail init` creates `.taskrail/` at the repository root:

| File | Commit it? | Purpose |
|---|---|---|
| `policy.yaml` | yes | Models, gates, escalation rules, budget |
| `executors.yaml` | yes | Adapters for coding-agent CLIs |
| `prices.yaml` | yes | Model prices used for cost accounting |
| `ledger.db` | no | SQLite cost and outcome ledger |
| `runs/`, `tmp/` | no | Per-run artifacts and gate reports |

Commands find `.taskrail/` by searching upward from the current directory, or take `--config <dir>`. Unknown keys are errors, reported with a line number. `taskrail doctor` validates everything.

## policy.yaml

```yaml
models:
  architect: ""                # strong model id at the provider
  worker: ""                   # cheap model, passed to the executor as a model override

provider:
  base_url: https://openrouter.ai/api/v1   # any OpenAI-compatible endpoint
  api_key_env: OPENROUTER_API_KEY          # the variable NAME, never the key

executors:
  default: opencode            # a key in executors.yaml

prepare:                       # once per run in the fresh worktree, before the baseline; network allowed
  - { name: deps, cmd: ["npm", "ci"] }

gates:                         # argument arrays, never shell strings
  - { name: lint, cmd: ["ruff", "check", "."] }
  - { name: test, cmd: ["pytest", "-q", "--junitxml=.taskrail/tmp/pytest.xml"], report: ".taskrail/tmp/pytest.xml" }
  - { type: locked_regions }

escalation:
  max_retries_per_unit: 2      # failed attempts on a unit before escalation (attempt 1 fails → retry; attempt 2 fails → escalate)
  protected_paths: ["src/auth/**", "migrations/**"]   # .taskrail/** is always protected
  max_diff_lines: 400          # larger diffs are rejected and the unit is split
  review_on_new_dependency: true
  worker_timeout_seconds: 1200

budget:
  max_usd_per_task: 3.00       # hard abort
  max_usd_per_run: 20.00       # optional
  max_architect_token_share: 0.30

isolation:
  worker_env_allow: ["MY_TOOL_HOME"]   # extra env var names workers may see; secret-looking names are still dropped
  keep_worktree: false                 # or `taskrail run --keep-worktree`
```

### Supervisor rules

Checked in order after every attempt; the first match decides.

| # | Condition | Decision |
|---|---|---|
| 1 | Diff larger than `max_diff_lines` | Split (the unit is mis-sized) |
| 2 | Diff touches `.taskrail/**` or a `protected_paths` glob | Takeover |
| 3 | New dependency and `review_on_new_dependency` | Takeover |
| 4 | Worker timed out | Takeover |
| 5 | No changes | Retry, or Takeover at the limit |
| 6 | A gate has new failures | Retry with gate output as feedback, or Takeover at the limit |
| 7 | Otherwise | Accept: the unit is committed on `taskrail/<run-id>` |

The worker's exit code and self-report are ignored; only the git diff and gates count. Takeover and Split land in M2; until then they fail the unit.

### Gates

| Key | Default | Meaning |
|---|---|---|
| `type` | `command` | `command` or `locked_regions` |
| `name` | command text | Display name |
| `cmd` | — | Argument array |
| `report` | none | TRX or JUnit XML written by the command. Lets Taskrail compare failing **test IDs** with the baseline. |
| `timeout_seconds` | 900 | Per-gate timeout |
| `network` | false | Declares that the gate needs network access (documentation only in v1) |

**Baselining.** Before any worker runs, every gate runs on the base commit. A gate blocks a unit only for failures that are new compared with that baseline. A non-test gate that already fails on the base commit is downgraded to a warning, so fix it or remove it.

## executors.yaml

```yaml
executors:
  opencode:
    cmd: ["opencode", "run", "{prompt}"]
    model_args: ["--model", "taskrail/{model}"]   # appended only when a worker model is set
    brief_file: AGENTS.md                  # the agent's native instructions file
    version_cmd: ["opencode", "--version"]
    tested_version: "x.y.z"                # doctor warns when the installed version differs
    env:                                   # extra env for the worker
      TASKRAIL_PROXY_TOKEN: "{proxy_token}"
    files:                                 # written for one attempt, then restored; never committed
      opencode.json: |
        { "provider": { "taskrail": { "options": { "baseURL": "{proxy_url}" } } } }
    capabilities:
      headless: true
      model_override: true
      structured_output: false
      session_resume: false
      reports_tokens: false
      permission_modes: true
      proxy_routable: true                 # LLM traffic can be pointed at Taskrail's metering proxy
```

Placeholders in `cmd`, `model_args`, `env` and `files`:

| Placeholder | Value |
|---|---|
| `{prompt}` | Short fixed instruction pointing at the brief file |
| `{model}` | Worker model (the architect model for strategies 1 and 1b) |
| `{workdir}` | Worktree path |
| `{proxy_url}` | Metering proxy, OpenAI-style base ending in `/v1` |
| `{proxy_root}` | Metering proxy origin, for clients that append `/v1/...` (e.g. `ANTHROPIC_BASE_URL`) |
| `{proxy_token}` | Per-run token the proxy accepts in place of a real key |

Other braces, such as OpenCode's `{env:VAR}`, are left untouched.

The brief is appended to an existing brief file (e.g. a repo's own `AGENTS.md`) and the original is restored after each attempt.

Workers run with a **scrubbed environment**: an allowlist of OS and toolchain variables (PATH, HOME/USERPROFILE, TEMP, locale, SystemRoot, JAVA_HOME, GOPATH, DOTNET_ROOT, proxy settings, …), plus `isolation.worker_env_allow` and the `env` entries above. Names that look like credentials are always dropped, and provider keys are never passed. Gates inherit the parent environment minus credential-looking variables. See [threat-model.md](threat-model.md).

When `proxy_routable: true`, Taskrail starts a metering proxy on 127.0.0.1 for the run. The worker reaches the provider through it with the per-run token, and every call's tokens and cost are recorded. Executors that cannot be routed through the proxy are recorded as **estimated**.

## prices.yaml

```yaml
models:
  "provider/model-id":
    input: 0.00          # USD per million input tokens
    output: 0.00         # USD per million output tokens
    cached_input: 0.00   # optional; defaults to input
```

Taskrail ships no built-in prices because they go stale. Calls to unpriced models are recorded and flagged as **estimated**.
