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

gates:                         # argument arrays, never shell strings
  - { name: lint, cmd: ["ruff", "check", "."] }
  - { name: test, cmd: ["pytest", "-q", "--junitxml=.taskrail/tmp/pytest.xml"], report: ".taskrail/tmp/pytest.xml" }
  - { type: locked_regions }

escalation:
  max_retries_per_unit: 2
  protected_paths: ["src/auth/**", "migrations/**"]
  max_diff_lines: 400          # larger diffs are rejected and the unit is split
  review_on_new_dependency: true
  worker_timeout_seconds: 1200

budget:
  max_usd_per_task: 3.00       # hard abort
  max_usd_per_run: 20.00       # optional
  max_architect_token_share: 0.30
```

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
    model_args: ["--model", "{model}"]     # appended only when a worker model is set
    brief_file: AGENTS.md                  # the agent's native instructions file
    version_cmd: ["opencode", "--version"]
    tested_version: "x.y.z"                # doctor warns when the installed version differs
    env:                                   # extra env for the worker; {proxy_url} and {proxy_token} available
      SOME_BASE_URL: "{proxy_url}"
    capabilities:
      headless: true
      model_override: true
      structured_output: false
      session_resume: false
      reports_tokens: false
      permission_modes: true
      proxy_routable: true                 # LLM traffic can be pointed at Taskrail's metering proxy
```

Workers run with a **scrubbed environment**: a small allowlist (PATH, HOME/USERPROFILE, TEMP, locale, SystemRoot) plus the `env` entries above. Provider keys are never passed. See [threat-model.md](threat-model.md).

## prices.yaml

```yaml
models:
  "provider/model-id":
    input: 0.00          # USD per million input tokens
    output: 0.00         # USD per million output tokens
    cached_input: 0.00   # optional; defaults to input
```

Taskrail ships no built-in prices because they go stale. Calls to unpriced models are recorded and flagged as **estimated**.
