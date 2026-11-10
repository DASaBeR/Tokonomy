# Configuration

`tokonomy init` creates `.tokonomy/` at the repository root:

| File | Commit it? | Purpose |
|---|---|---|
| `policy.yaml` | yes | Models, gates, escalation rules, budget |
| `executors.yaml` | yes | Adapters for coding-agent CLIs |
| `prices.yaml` | yes | Model prices used for cost accounting |
| `ledger.db` | no | SQLite cost and outcome ledger |
| `runs/`, `tmp/` | no | Per-run artifacts and gate reports |

Commands find `.tokonomy/` by searching upward from the current directory, or take `--config <dir>`. Unknown keys are errors, reported with a line number. `tokonomy doctor` validates everything.

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

prepare:                       # once per run on the base commit, before the baseline (see "Gate execution")
  - { name: deps, cmd: ["npm", "ci"] }

gates:                         # argument arrays, never shell strings
  - { name: lint, cmd: ["ruff", "check", "."] }
  - { name: test, cmd: ["pytest", "-q", "--junitxml=.tokonomy/tmp/pytest.xml"], report: ".tokonomy/tmp/pytest.xml" }
  - { type: locked_regions }

escalation:
  max_retries_per_unit: 2      # failed attempts on a unit before escalation (attempt 1 fails → retry; attempt 2 fails → escalate)
  max_takeovers_per_unit: 1    # architect takeovers per unit; 0 disables takeover
  protected_paths: ["src/auth/**", "migrations/**"]   # .tokonomy/** is always protected
  max_diff_lines: 400          # larger diffs are rejected and the unit is split
  review_on_new_dependency: true
  worker_timeout_seconds: 1200
  final_review: false                  # one advisory architect review of the combined diff after every unit passed

budget:
  max_usd_per_task: 3.00       # hard abort
  max_usd_per_run: 20.00       # optional
  max_architect_token_share: 0.30

isolation:
  worker_env_allow: ["MY_TOOL_HOME"]   # extra env var names workers may see; secret-looking names are still dropped
  keep_worktree: false                 # or `tokonomy run --keep-worktree`
  worktree_root: "D:/tokonomy-worktrees"   # optional; default %LOCALAPPDATA%\tokonomy\worktrees (see "Worktree location")

gate_execution:                # REQUIRED when there are gates or prepare steps; see "Gate execution"
  provider: windows-sandbox    # windows-sandbox | host-unsafe
  network_policy: none         # gates: none | full
  prepare_network: full        # prepare steps: none | full
```

### Supervisor rules

Checked in order after every attempt; the first match decides.

| # | Condition | Decision |
|---|---|---|
| 1 | Diff larger than `max_diff_lines` | Split (the unit is mis-sized) |
| 2 | Diff touches `.tokonomy/**` or a `protected_paths` glob | Takeover |
| 3 | New dependency and `review_on_new_dependency` | Takeover |
| 4 | Worker timed out | Takeover |
| 5 | No changes | Retry, or Takeover at the limit |
| 6 | A gate has new failures | Retry with gate output as feedback, or Takeover at the limit |
| 7 | Otherwise | Accept: the unit is committed on `tokonomy/<run-id>` |

The worker's exit code and self-report are ignored; only the git diff and gates count.

### Architect takeover (strategy 4)

When a unit keeps failing its gates (rule 6), the worker makes no changes, or it times out, the strong model steps in once per unit (`escalation.max_takeovers_per_unit`, 0 turns it off):

1. It is **not** an agent. It receives the contract and rules, the unit spec, the worker's diff, the failure output and the current text of the touched files, and answers with **search/replace edits** for the failing section only. (Search/replace applied cleanly 5/5 times on real failures; unified diffs 4/5.)
2. The edits are applied all-or-nothing: each SEARCH must match exactly once (a second try ignores trailing whitespace and line endings), paths cannot leave the worktree or touch `.git` / `.tokonomy`. If an edit cannot be applied the architect is asked once more with the error.
3. The **full gate suite re-runs**. If it passes, the unit is committed (`[architect takeover]`); if not, the unit fails and the worktree is reset.
4. What the architect wrote becomes a **locked region**: later units fail the `locked_regions` check if those lines change, and the worker gets the lock in its retry feedback.
5. `handoff.yaml` is written to `.tokonomy/runs/<run-id>/` (the architect's summary, why the worker failed, the locks, the next unit) and the next workers' briefs include it. Every worker starts a fresh session.

Takeover cost is counted in the ledger as architect spend and against the budget caps.

**Not handled by takeover:** a diff that touches a protected path or adds a dependency is a human decision, so the unit fails with a message saying so. An oversized diff (rule 1) also fails: splitting such a unit is not implemented yet.

### Gates

A **gate** is an automatic pass/fail check that runs on the worker's output before a unit is accepted: a build, a linter, a type check, your test suite. It is a command (an argument array, never a shell string), so the model's own report of success never decides anything. A unit is accepted only when every gate passes; a failing gate sends the worker back with the gate's output (retry), and repeated failure escalates to the architect (takeover). `tokonomy init` proposes gates for the stack it detects; add your own under `gates:`. Projects without tests can let the architect write them: see [Acceptance tests](#acceptance-tests-written-by-the-architect). Gates run in an isolated environment, see [Gate execution](#gate-execution). Other terms: [glossary](glossary.md).

| Key | Default | Meaning |
|---|---|---|
| `type` | `command` | `command` or `locked_regions` |
| `name` | command text | Display name |
| `cmd` | — | Argument array |
| `report` | none | TRX or JUnit XML written by the command. Lets Tokonomy compare failing **test IDs** with the baseline. |
| `timeout_seconds` | 900 | Per-gate timeout |
| `network` | false | Declares that the gate needs network access. Documentation only: network is set for all gates by `gate_execution.network_policy`. |

**Baselining.** Before any worker runs, every gate runs on the base commit. A gate blocks a unit only for failures that are new compared with that baseline. A non-test gate that already fails on the base commit is downgraded to a warning, so fix it or remove it.

### Gate execution

Gates and prepare steps run code from the worktree: tests, build scripts and package lifecycle scripts, which is code the worker wrote. `gate_execution` decides where that code runs. **There is no default and no fallback.** A policy with gates but no `gate_execution` section makes `run` and `bench` stop before any worker starts, and so does an unavailable sandbox.

```yaml
gate_execution:
  provider: windows-sandbox        # or host-unsafe
  network_policy: none             # gates: none (default) | full
  prepare_network: full            # prepare steps on the base commit: full (default) | none
  startup_timeout_seconds: 180     # boot + readiness
  resource_limits:
    memory_mb: 4096                # per sandbox VM (Windows Sandbox has no CPU limit)
  artifact_limits:
    max_snapshot_mb: 512           # source copied into the VM
    max_dependencies_mb: 4096      # archive produced by prepare
    max_log_kb: 1024               # tail kept per command
    max_report_mb: 16              # test report collected per command
  cleanup_policy: always           # always | keep-on-failure (keeps host-side folders; the VM is always destroyed)
  toolchains: []                   # host folders mapped read-only, e.g. ["C:/Program Files/dotnet"]
```

| Provider | What happens |
|---|---|
| `windows-sandbox` | Each gate batch (the baseline, every attempt, every takeover check) gets a **fresh Windows Sandbox VM**, destroyed right after. The VM receives a copy of the worktree's files (what git lists: no `.git`, no `.tokonomy`, no ignored files, nothing reached through a link) mapped **read-only**, plus read-only toolchain folders and one empty writable results folder. The worktree, repository, profile, temp and other drives are never mapped. Networking is off unless `network_policy: full`; clipboard, printer, audio, video and GPU are always off. |
| `host-unsafe` | Commands run in the worktree on this machine with your privileges and a scrubbed environment (the behavior before gate isolation). For development, tests and trusted tasks. `run`, `doctor` and config validation all warn about it. |

**Prepare and dependencies.** With `windows-sandbox`, prepare steps run once in their own VM on the base commit, before any worker, with `prepare_network`. Everything they create that is not part of the source (`node_modules`, `.venv`, `obj/`), plus the package caches (NuGet, npm, pip), is packed into one archive inside that VM. The host stores the archive **without unpacking it** and maps it read-only into every gate VM, which unpacks it before copying the source. Gates then run offline. Conventions that make this work:

- .NET: `prepare: [{ cmd: ["dotnet", "restore"] }]`, gates with `--no-restore` (`dotnet build --no-restore`, `dotnet test --no-restore`).
- Node: `prepare: [{ cmd: ["npm", "ci"] }]`; gates `npm test`.
- Python: install into a virtualenv in the worktree, not into the (read-only) interpreter: `prepare: [{ cmd: ["python", "-m", "venv", ".venv"] }, { cmd: [".venv/Scripts/python", "-m", "pip", "install", "-r", "requirements.txt"] }]`, gates `[".venv/Scripts/python", "-m", "pytest"]`.
- Files a prepare step *changes* (rather than adds) are not carried over, because the source copy always wins.
- Private feeds are not supported in isolated mode: credentials are never forwarded into a VM.

**Toolchains.** Windows Sandbox boots a clean Windows with no SDKs. Tokonomy maps the host's own installations into it read-only, at `C:\tk\tools\t0`, `t1` and so on, and puts them on `PATH` (with `bin`, `cmd` and `Scripts` subfolders, and `DOTNET_ROOT` for .NET). With `toolchains: []` the folders are derived from the gate and prepare commands: `dotnet` maps `C:\Program Files\dotnet`, `python` maps the folder holding `python.exe`, and `git` in `Git\cmd` maps `Git`. Windows' own tools need no mapping, and relative commands (`.venv/Scripts/python`) come from the working copy. A folder is **refused** if it is under your profile (this covers pyenv, `%USERPROFILE%\.dotnet`, npm global CLIs in `%APPDATA%\npm`, and user-scope Python installs), in temp, in the repository or a Tokonomy working folder, a drive root, or the Windows directory. Install such toolchains machine-wide or list a suitable folder explicitly.

**Inside the VM** each command runs from `C:\work` (the VM's own copy of the source) with a fixed environment: `USERPROFILE`/`HOME` set to a scratch folder, `TEMP` set to scratch, `NUGET_PACKAGES`/`npm_config_cache`/`PIP_CACHE_DIR` set to the cache folder, `CI=true`, telemetry off. Nothing from your environment is passed in. A command's timeout (`timeout_seconds` per gate) is enforced inside the VM with a process-tree kill. If the VM does not answer within the timeout plus 90 seconds, the host destroys it, and the next gate in the batch gets a fresh one.

**What the host trusts.** Only fixed file names from the results folder, opened without following links and size-capped. A result counts only with a status carrying the environment's random nonce. A missing or inconsistent status is a failure, never a pass. A link in the results folder, or a link or hard link in the source, fails the unit as a workspace violation (never retried, never taken over). Test results can still be forged by the tests themselves; see [threat-model.md](threat-model.md) (G13).

**Cleanup and recovery.** Every environment is recorded in `%LOCALAPPDATA%\tokonomy\gate-envs\<env>\state.json` before its VM starts. When a run crashes, the next run (and `tokonomy doctor --remove-leftover-gate-environments`) stops that VM, confirms it is gone, and deletes the folders without following links. Sandboxes Tokonomy did not start are never stopped. Windows Sandbox runs one instance at a time, so close your own sandbox window before a run.

Setup, measurements and troubleshooting: [gate-isolation.md](gate-isolation.md).

## executors.yaml

```yaml
executors:
  opencode:
    cmd: ["opencode", "run", "{prompt}"]
    model_args: ["--model", "tokonomy/{model}"]   # appended only when a worker model is set
    brief_file: AGENTS.md                  # the agent's native instructions file
    version_cmd: ["opencode", "--version"]
    tested_version: "x.y.z"                # doctor warns when the installed version differs
    env:                                   # extra env for the worker
      TOKONOMY_PROXY_TOKEN: "{proxy_token}"
    files:                                 # written for one attempt, then restored; never committed
      opencode.json: |
        { "provider": { "tokonomy": { "options": { "baseURL": "{proxy_url}" } } } }
    capabilities:
      headless: true
      model_override: true
      structured_output: false
      session_resume: false
      reports_tokens: false
      permission_modes: true
      proxy_routable: true                 # LLM traffic can be pointed at Tokonomy's metering proxy
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

Workers run with a **scrubbed environment**: an allowlist of OS and toolchain variables (PATH, HOME/USERPROFILE, TEMP, locale, SystemRoot, JAVA_HOME, GOPATH, DOTNET_ROOT, proxy settings, …), plus `isolation.worker_env_allow` and the `env` entries above. Names that look like credentials are always dropped (keys, tokens, passwords, cookies, sessions, `DATABASE_URL`, connection strings, and agent sockets such as `SSH_AUTH_SOCK` / `GPG_AGENT_INFO`, which let a child process authenticate as you), and so is any **value** that carries credentials (a URL with `user:password@`, e.g. an authenticated `HTTPS_PROXY`, or `Password=` in a connection string), even under an allowlisted name. Provider keys are never passed. Gates inherit the parent environment minus the same names and values. Tokonomy's own git commands additionally drop every `GIT_*` variable, so a `GIT_DIR` or `GIT_WORK_TREE` left in your shell cannot redirect them, and they address the worktree with explicit `--git-dir`/`--work-tree` rather than trusting its `.git` file. See [threat-model.md](threat-model.md).

When `proxy_routable: true`, Tokonomy starts a metering proxy on 127.0.0.1 for the run. The worker reaches the provider through it with the per-run token, and every call's tokens and cost are recorded. Executors that cannot be routed through the proxy are recorded as **estimated**.

**Architect provider.** `architect_provider.kind` chooses how architect calls (planning, takeover, final review) are made. `api` (default) uses the OpenAI-compatible `provider` with a key. `claude-cli` runs Claude Code headless on your own login, with `models.architect` passed to `claude --model` (for example `claude-opus-5-5`): no API key, no tools, no MCP servers and no user settings are loaded, so each call carries about 1k tokens of overhead instead of about 35k. The prompt goes in on stdin and the system prompt from a file, so size and quoting never touch the command line. Its cost is Claude Code's own API-equivalent figure and is always recorded as **estimated**. Worker traffic still uses `provider`, so you can point it at another service (for example OpenRouter) while the architect runs on Claude Code.

**The answering model is verified.** Claude Code reports the models it used. The requested model must be among those that produced output, otherwise the call fails with an error that names what answered instead; nothing is attributed to the requested model after a fallback. A full id (`claude-opus-5-5`) must match exactly (a `[1m]` context suffix or a date stamp is ignored) and an unverifiable full id is refused. An alias (`opus`) matches any model of that family. Claude Code also spends a few hundred tokens on a cheaper helper model on every request; that is counted in the cost but never mistaken for the answerer.

```yaml
architect_provider:
  kind: claude-cli      # command: claude (default)
models:
  architect: claude-opus-5-5
```

## Acceptance tests written by the architect

`gates` are commands you provide. `acceptance` lets the strong model write the tests itself, so a project with no test suite (a landing page, a script) still gets a real definition of done, and the cheap worker has to meet it.

```yaml
acceptance:
  cmd: ["python", "-m", "unittest", "discover", "-s", "acceptance"]   # empty: feature off
  dir: acceptance                      # the architect's files must live here
  hint: "Plain HTML/CSS/JS site. Test with Python's standard library (html.parser); no packages."
  timeout_seconds: 300
```

What happens:

1. **Plan.** `tokonomy plan` asks the architect for `tests` on each unit: `{path, content}` files inside `dir`. A plan with no tests, or with a path outside `dir`, is rejected and the architect is asked again.
2. **Before each unit.** That unit's tests are written into the run worktree and committed on their own (`tokonomy(U1): acceptance tests`), so they are never part of the worker's diff. The worker brief lists them and says they are read-only. A unit's tests may only check what that unit and earlier units deliver: from unit N on, the tests of units 1..N all run.
3. **Every gate run.** Before the command runs, Tokonomy rewrites each architect file from the plan and deletes any other file in `dir`. Weakening a test, deleting it or adding a `conftest.py` does nothing except leave a warning and an `acceptance_tamper` record.
4. **Decision.** The command runs in the gate environment (see `gate_execution`) together with your other gates. It has **no baseline**: any failure blocks the unit, and the output goes back to the worker as feedback. Retry and takeover work as for any gate.

The tests are only as good as the architect's plan, and they are the architect's reading of your request, not yours. Review the committed `acceptance/` files in the run branch, as you would review any other code. A takeover edit by the architect is also checked against them.

## Connections: any model from any provider

The strong model (`models.architect`) and the cheap model (`models.worker`) are connected independently.

| Role | Connection | Config |
|---|---|---|
| strong | Claude Code subscription | `architect_provider: { kind: claude-cli }` |
| strong | Antigravity subscription | `architect_provider: { kind: agy-cli }` (the session may read only its scratch prompt file; no writes, no commands; the answering model is verified) |
| strong | any OpenAI-compatible API | `architect_provider: { kind: api, base_url: ..., api_key_env: NAME }` (its own endpoint and key) |
| cheap | Claude Code subscription | `executors.default: claude-code-sub` |
| cheap | Antigravity subscription | `executors.default: antigravity` |
| cheap | any OpenAI-compatible API | `executors.default: opencode` and `provider: { base_url, api_key_env }` (metered through Tokonomy's proxy) |

Antigravity (`agy`) has no API-key option, so a Gemini API key is a separate connection through Google's OpenAI-compatible endpoint. The services Tokonomy has run end to end are OpenRouter (strong and cheap) and the CLI logins; Anthropic, Gemini, OpenAI, DeepSeek and custom endpoints use the same protocol but are marked "not tested live". API keys are only ever read from the environment variable named in the config, and are removed from worker and gate environments.

`tokonomy init` scans the machine, asks for both choices, checks keys, tests the CLI logins on request, and writes all of the above.

## Claude architect + Antigravity worker

Both roles can run on subscriptions you already have, with no API key and no metering proxy.

```yaml
# policy.yaml
models:
  architect: claude-opus-5-5
  worker: gemini-3.8-flash-medium     # `agy models` lists the ids
architect_provider:
  kind: claude-cli
executors:
  default: antigravity
gates:
  - { name: test, cmd: ["python", "-m", "unittest"] }
gate_execution:
  provider: windows-sandbox           # the tests the worker writes run in a disposable VM
escalation:
  final_review: true                  # one advisory architect review of the combined diff
# isolation.worktree_root is not needed: the default is already outside temp
```

The `antigravity` entry that `tokonomy init` writes to `executors.yaml`:

```yaml
antigravity:
  cmd: ["agy", "-p", "{prompt}", "--output-format", "stream-json", "--settings", "{settings_file}", "--sandbox"]
  model_args: ["--model", "{model}"]
  brief_file: AGENTS.md
  usage_format: agy-json
  verify_model: true
  permissions:
    commands: []                # extra exact command lines the worker may run
    allow_gate_commands: false  # see "Shell commands" below before turning this on
    scope_files_to_worktree: true
    deny_temp_writes: true
    deny_temp_reads: true
    allow: []                   # raw extra rules
    deny: []
```

**Finding `agy`.** The command is looked up on `PATH` first. The Antigravity installer puts `agy.exe` in `~/.gemini/bin`, which is often not on `PATH` and never is in a scrubbed worker environment, so after a `PATH` miss Tokonomy also checks `~/.gemini/bin`, `~/.local/bin` and `~/.antigravity/bin`. An absolute path in `cmd` always works. `tokonomy doctor` reports what it found, checks that `models.worker` is listed by `agy models`, and reports the effective worktree root.

**Permissions.** Headless `agy` cannot ask, so nothing is approved interactively and `--dangerously-skip-permissions` is never used. Tokonomy writes a per-attempt `--settings` file (next to the run logs, outside the worktree, so the worker cannot rewrite its own rules) containing:

| Rule | Why |
|---|---|
| `read_file(<worktree>)`, `write_file(<worktree>)` | Edits only inside the worktree. No edit mode (`--mode accept-edits`) is used, because it also lets files outside the worktree be written. |
| no `command(...)` rule by default | The worker runs no shell commands at all (see "Shell commands"). With `allow_gate_commands` or `permissions.commands`, one literal rule per exact command line. |
| `deny write_file(<worktree>/.git)`, `deny write_file(<worktree>/.tokonomy)` | The worker can never rewrite the worktree's git pointer, install git hooks or touch Tokonomy's state. agy matches deny rules by whole path segment, so `.gitignore` stays editable (verified). |
| `deny read_file` + `write_file` for every link in the worktree that leads outside it | agy's permission check is lexical: it **follows** a junction or symlink inside the worktree to whatever it points to (verified: it read a file outside through a junction). Links present after `prepare` (e.g. a pnpm store) are denied by their own path. |
| `deny write_file` for git-ignored content (`node_modules/`, `build/`, ...) | Changes there never appear in a diff, so a worker could hide something a gate later runs. |
| `deny read_file` + `write_file` for `~/.gemini`, `~/.claude`, `~/.claude.json`, `~/.ssh`, `~/.aws`, `~/.config`, `~/.git-credentials`, `~/.netrc`, `%APPDATA%`, ... | Defense in depth: outside the worktree is refused anyway, but both agents' own logins are named explicitly. Only the model's tool calls are affected; agy still reads its own login (verified). |
| `deny write_file(<temp>)` and `deny read_file(<temp>)` | agy treats the temp directory as scratch space any run can read and write. |
| anything in `permissions.allow` / `permissions.deny` | Your additions. A deny always beats an allow. |

`command(...)` rules are **literal and whole-line** in agy 1.5.3 (verified): `command(python -m unittest)` allows exactly `python -m unittest`, not `python -m unittest -v`, and `command(a b c)` never matches `a b c; d`. There are no prefix rules and regex characters are not interpreted (`(-q|-v)`, `-[qv]`, `.*` all failed to match), despite the CLI's own text calling tokens anchored regexes. So each allowed command is one exact string, and a command containing parentheses cannot be granted (Tokonomy skips it and records a note).

`run_command` in agy 1.5.3 rejects a working-directory argument (`additional properties 'Cwd' not allowed`, verified), so commands run in the worktree; a reported `Cwd` outside it is treated as a fault in case a later version accepts one.

**Shell commands.** `allow_gate_commands` is **off** by default. A test or build command executes code from the worktree, which is code the worker wrote: its tests, or a `unittest.py` that shadows the standard library (Python searches the current directory first). Allowing `python -m unittest` therefore gives the worker arbitrary code execution inside its session, with your privileges: it could read a credential file and print it into the model's context, or create a junction mid-run and remove it before Tokonomy looks. Literal rule matching cannot prevent that, because the command line is exactly the allowed one. Without commands the worker edits files and Tokonomy runs the gates after the attempt. Turn it on only for repositories and tasks you trust; `doctor` warns when it is on.

The worker's brief ends with a *Tool permissions* section listing what it may do, because the **first refused call ends the whole headless session** with nothing saved (agy auto-loads `AGENTS.md`, so a well-behaved model reads it and stays inside the rules).

**How a bad run is detected.** agy reports nearly every failure with exit code 0 and `status: SUCCESS`. Tokonomy parses `--output-format stream-json` and marks the attempt as faulted when any of these holds:

| Signal | Fault |
|---|---|
| process exit is non-zero, no result printed, or `status` is not `SUCCESS` | `ProcessFailed` / `NoResult` / `ReportedError` |
| the session's `init` model differs from the requested one, or no model is reported | `ModelMismatch` |
| agy rejects the model id (`invalid model selection`) | `ModelMismatch` with agy's message |
| `denied_actions` is non-empty (no rule matched; headless auto-deny) | `PermissionDenied` |
| a shell command ran or was attempted that is not on the allowlist (`denied_actions` only says `RunCommand`; the command comes from the tool events) | `PermissionDenied` |
| a file tool errored on a path outside the worktree or on a path one of Tokonomy's deny rules names (an explicit deny rule yields `SUCCESS` with an empty `denied_actions`; found live when a refused `.git/hooks` write came back looking like success) | `PermissionDenied` |
| a command reports a working directory outside the worktree | `PermissionDenied` |
| a new link appeared that leads outside the worktree, a known link was retargeted, the `.git` pointer changed, or a file Tokonomy wrote was replaced by a link (checked after the attempt and again after the gates) | `WorkspaceViolation` |
| a worker that ran no shell commands created git-ignored files (they are deleted) | `PermissionDenied` |
| the final response quotes a permission-rule refusal | `PermissionDenied` |

The supervisor never accepts a faulted attempt, even when the diff passes every gate. Faults are retried (the fault text goes into the retry brief, including the exact commands that are allowed) and, after `max_retries_per_unit`, escalate to an **explicit** architect takeover that is recorded in the ledger and labels the commit `[architect takeover]`. A `ModelMismatch` or `WorkspaceViolation` is different: it fails the unit immediately, is never retried and never handed to the architect, so a misconfigured or substituted worker model can never turn into a silent switch to Opus, and nothing from an attempt that tried to leave its worktree is built on. After a `WorkspaceViolation` the gates are not run at all, because they would execute code from that worktree. Every attempt's conversation id, reported model, shell commands, denied actions and isolation notes are in `transcript.jsonl`.

**Isolation.** Verified against agy 1.5.3 with only worktree grants: reads and writes outside the worktree, `..` paths and sibling directories are refused, **except** the system temp directory (denied explicitly by Tokonomy, see above) and anything reachable through a link inside the worktree (denied per link, see above). `--sandbox` is passed as defense in depth, but its effect could not be observed on Windows and is not relied on. See [threat-model.md](threat-model.md) for what remains.

## Worktree location

Each run gets a git worktree under `<root>/<hash of repo path>/<run id>`. The root is:

| Platform | Default root |
|---|---|
| Windows | `%LOCALAPPDATA%\tokonomy\worktrees` |
| Linux | `~/.local/share/tokonomy/worktrees` |
| macOS | `~/Library/Application Support/tokonomy/worktrees` |

or `isolation.worktree_root` (a relative value resolves against the repository root; environment variables are expanded). The root is validated when the config loads and again before every worktree is created, both as written and with any links in it resolved. It is refused when it:

- is inside, equal to or contains the temp directory (`%TEMP%`, `%TMP%`, `TMPDIR`, `C:\Windows\Temp`, `/tmp`, `/var/tmp`), which some agents let any run read and write;
- overlaps the repository (inside it, or containing it);
- is or contains your home directory, or is a drive or filesystem root;
- overlaps system or credential locations: the Windows and Program Files directories, ProgramData, `%APPDATA%`, `~/.ssh`, `~/.gnupg`, `~/.aws`, `~/.azure`, `~/.kube`, `~/.docker`, `~/.config`, `~/.gemini`, `~/.claude`, `~/.antigravity`, `/etc`, `/usr`, ...

`tokonomy doctor` prints the effective root and whether it is safe.

**Cleanup.** At the end of a run Tokonomy removes every link inside the worktree (the link itself, never its target) before deleting the worktree, because `git worktree remove --force` follows junctions on Windows and deletes what they point to (verified with git for Windows). If a link cannot be removed the worktree is left on disk and the run reports it. The per-repository folder is removed when its last worktree is gone.

**Moving worktrees out of temp (upgrading).** Earlier versions created worktrees in `%TEMP%\tokonomy\...`. Nothing is moved automatically: run branches (`tokonomy/<run id>`) stay in your repository either way, and new runs simply use the new root. Worktrees left behind by interrupted runs show up in `tokonomy doctor` as *leftover worktrees*; remove the ones whose runs are finished with `tokonomy doctor --remove-leftover-worktrees`, which skips runs the ledger still marks as running and uses the same link-safe removal. Do **not** remove them with plain `git worktree remove --force`. If you had set `isolation.worktree_root` to something inside temp or inside the repository, `tokonomy` now refuses to start until you change it.

**Final review.** With `escalation.final_review: true`, one architect call reads the task, the rules, how each unit went and the combined diff after every unit has passed, and answers `APPROVE` or `CONCERNS`. It is **advisory**: it is recorded, shown by `run` and written to the transcript, but never changes the run status, because gates outrank opinions. A failed or unreadable review is recorded as unavailable.

**Unmetered executors that report usage.** Set `usage_format: agy-json` for the Antigravity CLI (tokens only, no cost; thinking tokens count as output). Set `usage_format: claude-json` and Tokonomy reads tokens and cost from Claude Code's `--output-format json` result. The figure is the agent's own API-equivalent estimate, so it is always recorded as **estimated**. The scaffolded `claude-code-sub` adapter uses this for Claude Code on a subscription login (no API key, no proxy, no endpoint override). A subscription is not billed per token, so treat its numbers as a size indicator, and check your plan's terms before running automated batches.

## prices.yaml

```yaml
models:
  "provider/model-id":
    input: 0.00          # USD per million input tokens
    output: 0.00         # USD per million output tokens
    cached_input: 0.00   # optional; defaults to input
```

Tokonomy ships no built-in prices because they go stale. Calls to unpriced models are recorded and flagged as **estimated**.

## Proxy (per user)

Behind V2RayN or any other proxy? Set it once; it applies to every project and every connection:

```
tokonomy proxy set socks5://127.0.0.1:10808     # or 127.0.0.1:10809 / http://... for an HTTP proxy
tokonomy proxy test                              # the proxy, then each model/subscription you use, through it
tokonomy proxy test --live                       # also one tiny real call per model
tokonomy proxy off | on | (no argument = show)
```

It is saved in `%LOCALAPPDATA%\tokonomy\proxy.json`. `TOKONOMY_PROXY` (an address, or `off`) overrides it.
When on, everything goes through it: API planning and key checks, the metering proxy's calls to the model provider, and
every child process (Claude Code, Antigravity, git, gates, workers) via `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY`/`NO_PROXY`.
Claude Code and most CLI tools cannot speak SOCKS, so for a SOCKS proxy Tokonomy runs a small local HTTP bridge (HTTPS
tunnels only) and hands that to them. `localhost`/`127.0.0.1` always skip the proxy. Proxy credentials in the address are
stored in that file and are the one secret that workers also receive; prefer a proxy without a password.
