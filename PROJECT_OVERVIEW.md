# Tokonomy: what we built and how it works

> The folder on disk is still called `Taskrail`, but the project was renamed to **Tokonomy** (token + economy). The solution is `Tokonomy.slnx`, the CLI command is `tokonomy`, and the per-project folder is `.tokonomy/`.
>
> This file is a single walkthrough of the project. It was written from the source code and the existing docs (`README.md`, `docs/`, `PROJECT_BRIEF.md`) as of commit `0e59d14`. Where the code and the older docs disagree, the code wins, and the disagreement is flagged in [section 12](#12-known-gaps-and-inconsistencies).

---

## 1. What the project is

Tokonomy is an open-source **.NET 10 command-line tool** that lowers the cost of AI-assisted coding. It does not replace Claude Code, Codex, Gemini CLI or OpenCode. It sits on top of them and decides **which model does which part of the work**:

| Role | Also called | Job | Model type |
|---|---|---|---|
| **Strong model** | architect | Plans the work, writes acceptance tests, rescues units the cheap model could not finish, optionally reviews the final diff | Expensive (Claude Opus/Sonnet, GPT, Gemini Pro...) |
| **Cheap model** | worker | Writes the code, one small unit at a time | Cheap (DeepSeek Flash, Gemini Flash, GPT mini...) |
| **Gates** | (not a model) | Build / lint / tests that decide pass or fail | Plain commands |
| **Supervisor** | (not a model) | Deterministic policy: accept, retry, take over, or fail | Plain code, no LLM |

### The hypothesis

> A strong model used sparingly (plan, takeover, final review), plus a cheap model for the bulk of the editing, verified by deterministic gates, finishes tasks at comparable quality for much lower **cost per successful task** than a strong model alone.

It is a hypothesis, not a claim. The benchmark harness exists to test it. The metric is **cost per successful task** = total spend (attempts, retries, planning) ÷ trials that passed the hidden acceptance check. A cheap run that fails is not a saving, because its cost still counts.

### Four design rules

1. **LLMs produce content; policy makes decisions.** Plans and patches come from models. Accept / retry / takeover come from fixed rules in code.
2. **Gates outrank opinions.** A failing test beats any model's "I'm done".
3. **Judge by the git diff, not the agent's report.** The worker's exit code and self-report are ignored.
4. **Measure everything.** If it is not in the ledger, it did not happen.

---

## 2. The whole workflow, end to end

This is what happens when you run `tokonomy build "a landing page for a SaaS product"` (strategy 4, the default).

```
 You: a task in plain words
   │
   ▼
 0. SETUP / PREFLIGHT   first run: guided setup. Always: git ready? tools installed? logged in? sandbox available?
   │
   ▼
 1. PLAN                strong model → JSON contract (units, rules, don't-list, optional acceptance tests)
   │                     └─ may ask you up to 6 clarifying questions first (one round)
   ▼
 2. CONFIRM             you see the plan and answer "Build this now?"
   │
   ▼
 3. PREPARE             isolated worktree + branch; optional `prepare` commands (npm ci ...);
   │                     BASELINE: every gate runs once on the starting commit
   ▼
 4. FOR EACH UNIT, IN DEPENDENCY ORDER:
   │    4a. EXECUTE     cheap model edits the worktree from a written brief
   │    4b. INSPECT     link / .git tampering / hidden-file checks
   │    4c. GATE        build, lint, tests (in a Windows Sandbox VM) + architect's acceptance tests + locked-region check
   │    4d. DECIDE      Supervisor: Accept | Retry | Takeover | Fail
   │         ├─ Accept   → one commit for the unit
   │         ├─ Retry    → back to 4a with the gate output as feedback
   │         └─ Takeover → strong model applies exact search/replace edits, gates re-run, edit is locked, handoff written
   ▼
 5. FINAL REVIEW        (optional, off by default) strong model reads the combined diff; advisory only
   │
   ▼
 6. LEDGER + MERGE      tokens and cost per tier saved; you are offered `git merge tokonomy/<run-id>`
```

### Step by step, with the real behaviour

**Step 0: setup and preflight.** `BuildCommand` calls `ConfigLocator.LoadOrSetupAsync`. If there is no `.tokonomy/`, the guided setup (section 6) runs. Then Tokonomy creates the git repo and a first commit if the folder is not one yet, and runs the doctor's *preflight* (git, executor, API keys or logins, gate tools, Windows Sandbox). Any hard failure stops here, **before a single token is spent**, and names the exact command to fix it.

**Step 1: plan.** The strong model receives the task plus repository context and must answer with **only JSON**:

```json
{
  "units": [{"id":"U1","title":"...","spec":"...","depends_on":[],"files":["path"],"acceptance":["..."], "tests":[...]}],
  "rules": [{"id":"R1","text":"..."}],
  "dont":  ["..."]
}
```

Rules in the planner prompt: 1-6 small units, each ideally under 200 changed lines; concrete specs that name files; dependencies first; short rules; a "don't" list of tempting mistakes. The result is validated (valid ids, no cycles in `depends_on`, shape). If it is invalid, the architect is re-asked **once** with the validation errors. Two invalid answers in a row abort the plan. Every call is recorded in the ledger, even failures.

- **Clarifying questions.** In an interactive terminal the architect may reply with a `{"questions": [...]}` object instead of a plan: at most 6 questions, 2-4 options each, every question carries a `recommended` answer, and some can be multi-select. You answer (or skip), and it then returns the contract. One round only, and asking does not count as a failed attempt. `--no-questions` or `--yes` disables this.
- **Acceptance tests.** If the project has no test suite and `acceptance.cmd` is set, the architect must also write `tests: [{path, content}]` per unit (see section 4.3).
- The plan is saved to `.tokonomy/runs/<run-id>/plan.json` and a `Planned` run row is created in the ledger.

**Step 2: confirm.** The plan is shown (units, rules, planning cost) and you are asked `Build this now?`. Say no and the plan stays saved: run it later with `tokonomy run --plan <id>`.

**Step 3: prepare and baseline.**
1. The gate-execution provider is resolved first. If it is unusable the run stops with an error before any worktree or worker exists. There is **no fallback to running gates on your machine**.
2. A git worktree is created on branch `tokonomy/<run-id>`. Your own files are never touched until you merge.
3. A metering proxy starts (if the worker is proxy-routable), plus the executor adapter.
4. `prepare` steps (for example `npm ci`) run once on the base commit inside their own sandbox, with network allowed by default. What they produce is packed into one opaque archive that later gate sandboxes unpack; the host never unpacks it.
5. **Baseline:** every command gate runs once on the unmodified start commit. A gate later blocks a unit only for **new** failures compared with this baseline, so a test that was already broken does not stop the work.
6. Any links in the worktree that point outside it are recorded; the worker is denied access to them, and any link that appears later is a violation.

**Step 4: the unit loop** (details in sections 3 to 5).

**Step 5: final review.** With `escalation.final_review: true`, after every unit passed the strong model reads the combined diff and returns `Approve`, a warning verdict, or `Unavailable`. It costs one architect call, is shown to you, and **never changes the run status** (gates outrank opinions).

**Step 6: ledger and merge.** `FinishRun` records the status (`Pass`, `Fail`, `AbortedBudget`, `Error`). In `build`, a passing run is offered for merge (`--merge` merges without asking). If the merge conflicts it is aborted and nothing in your project changes. The merge summary hides `acceptance/` files.

---

## 3. How the strong model works

The strong model is called **only at specific moments**. It is never an open-ended agent that roams the repo.

### 3.1 Where the strong model is used

| Moment | Prompt | Reply it must give | What happens with the reply |
|---|---|---|---|
| **Planning** | `Planner.SystemPrompt` + repo context | A JSON contract (or JSON questions) | Parsed, validated, saved as the plan |
| **Takeover** | `TakeoverPrompt` | Search/replace edit blocks + `SUMMARY:` + `WHY:` | Edits applied, gates re-run, edit locked |
| **Final review** | `ReviewPrompt` | A verdict, summary and concerns | Shown to you; advisory only |
| **Doing the work itself** (strategies 1 and 1b only) | The task as one unit | Handled by an executor like any worker | Baseline for the benchmark |

### 3.2 How a strong-model call is made (the "architect provider")

`architect_provider.kind` in `policy.yaml` selects the connection. The strong and cheap models are connected **independently**, so you can mix providers freely.

| Kind | What it does | Cost reporting |
|---|---|---|
| `api` (default) | OpenAI-compatible endpoint with an API key from an environment variable (OpenRouter, DeepSeek, OpenAI, Gemini's compatible endpoint, AvalAI, custom...) | Metered |
| `claude-cli` | Runs **Claude Code headless on your own login**, no API key. No tools, no MCP servers, no user settings are loaded, so each call carries about 1k tokens of overhead instead of about 35k | Claude Code's own API-equivalent figure, marked *estimated* |
| `agy-cli` | Runs the **Antigravity** CLI headless on your login. The session may read only its scratch prompt file: no writes, no commands | Tokens, estimated |
| `codex-cli` | Runs `codex exec` in a read-only sandbox with an empty scratch folder (ChatGPT plan or an API key given to `codex login`) | Tokens, estimated |

**The answering model is verified.** Claude Code reports the models that produced output. A full id such as `claude-opus-5-5` must match exactly (a `[1m]` or date suffix is ignored); an alias such as `opus` matches any model of that family. If a different model answered, the call **fails** with an error naming what answered, and nothing is billed to the requested model after a silent fallback.

**Streaming and retries** (recent commit `f0944c0`): every provider streams, shows live progress, detects stalls, and retries once. During planning the spinner reads "`<model>` is planning (a few minutes; a big project can take 10+)".

### 3.3 Takeover in detail (the most important strong-model behaviour)

A takeover happens when the supervisor returns `Takeover` and: the strategy allows escalation (strategy 4), an architect provider exists, and `state.Takeovers < escalation.max_takeovers_per_unit` (default 1).

1. **What the architect sees.** The overall task, project rules, the don't-list, the unit spec and acceptance, **why it was called**, the worker's diff against the start of the unit, the failure output from the gates (last 4,000 characters, with credentials redacted), the **current text of every touched file** (up to 24,000 characters each, read through a guard that never follows links), the list of repo files (up to 150), and any earlier locked regions.
2. **What it must answer.** The smallest possible fix, as exact edit blocks:
   ```
   <<<<<<< SEARCH path/relative/to/repo.py
   exact lines currently in the file
   =======
   the replacement lines
   >>>>>>> REPLACE
   ```
   followed by `SUMMARY:` (what it changed) and `WHY:` (what the junior got wrong). An empty SEARCH section creates a new file.
3. **How edits are applied.** All-or-nothing. Each SEARCH must match exactly once (a second pass ignores trailing whitespace and line endings). Paths cannot leave the worktree or touch `.git` or `.tokonomy`. If an edit fails to apply, the architect is asked **once more** with the error message.
4. **Verification.** The **full gate suite re-runs** on the result. If any gate still fails, the takeover fails, the worktree is reset to the unit start, and the unit (and run) fails.
5. **If it passes:**
   - The architect's replacement text becomes a **locked region**. From now on a built-in gate (`locked_regions`) fails any later unit that changes those lines, and the lock is quoted in the worker's retry feedback.
   - A `handoff` is written (`handoff.yaml` and `handoff-<unit>.yaml`): what changed, why the worker failed, the locked regions, the rules to follow, and the next unit. Every next worker's brief includes it.
   - The unit is committed with the label `[architect takeover]`.
   - The ledger records the architect attempt (`executor = "architect"`) and counts its cost against the budget.

Design choice: search/replace was used instead of unified diffs because it applied cleanly 5/5 times on real failures versus 4/5 for unified diffs.

**What takeover does not handle.** A diff that touches a protected path or adds an unapproved dependency is a *human decision*, so the unit fails with a message naming the packages and telling you about `escalation.allowed_dependencies`. An oversized diff with no passing gate is `Split`, but splitting is **not implemented**, so it also fails.

---

## 4. How the cheap model works

### 4.1 What the worker is

The cheap model runs through an **executor**: a config-driven adapter around a coding-agent CLI. Tokonomy writes a brief, launches the CLI headless in the worktree, and then **ignores everything the CLI says about itself**. Only the git diff and the gates count.

Executors are defined in `.tokonomy/executors.yaml` (`ExecutorDefinition`):

| Field | Meaning |
|---|---|
| `cmd` | Argument array with `{prompt}`, `{model}`, `{workdir}` placeholders. Never a shell string |
| `model_args` | Added only when a model override is given |
| `brief_file` | The agent's own instruction file (`AGENTS.md`, `CLAUDE.md`, `GEMINI.md`) |
| `env`, `files` | Extra environment and per-attempt files (for example an `opencode.json` pointing at the metering proxy); restored afterwards |
| `usage_format` | How to read token usage from stdout: `claude-json`, `codex-json`, `agy-json` |
| `permissions` | For Antigravity: scoped read/write rules, temp-directory denies, allowed exact commands |
| `verify_model` | Fail the attempt if the agent reports a different model than requested (default on) |
| `capabilities` | `headless`, `model_override`, `reports_tokens`, `proxy_routable`... |

Built-in adapters: **OpenCode** (verified against 1.19.5), **Claude Code subscription** (`claude-code-sub`), **Antigravity** (`antigravity`, Gemini on a subscription), and **Codex** (`codex`). `executors.strong` can name a *different* executor for strategies 1 and 1b, for example Opus through Claude Code while the cheap model runs on Antigravity.

### 4.2 The brief the worker receives

`BriefWriter.Build` writes the brief into the agent's native file (appended to an existing `AGENTS.md`; the original is restored after each attempt). The worker prompt is a one-liner: *"Read AGENTS.md in the repository root and complete the work unit it describes. Do not commit."* The brief contains, in order:

1. Instructions: smallest change that satisfies the unit; do not commit or branch; do not leave the repo; gates, not your report, decide.
2. Overall task.
3. Its unit (id, title, spec, expected files, acceptance items).
4. Architect-written acceptance tests, marked **read-only**.
5. Rules (must follow) and the Don't list.
6. "Already done" units (committed; build on them).
7. **Handoff from the architect**, with locked regions, after any takeover.
8. On a retry: **"Previous attempt failed (attempt N)"** with the gate output (last 12,000 characters, credentials redacted). The worker's earlier changes are still in the tree.

Every worker invocation is a **fresh session**: no memory across units except what the brief carries.

### 4.3 What the worker is not allowed to do

- **Scrubbed environment.** Only an allowlist of OS and toolchain variables is passed (PATH, HOME, TEMP, JAVA_HOME, DOTNET_ROOT, proxy settings...). Anything credential-shaped (keys, tokens, passwords, cookies, `DATABASE_URL`, `SSH_AUTH_SOCK`, or a value like `https://user:pass@host`) is dropped. Provider keys are **never** passed.
- **No real key.** An OpenAI-compatible worker talks to the **metering proxy** on 127.0.0.1 with a per-run token. The real key lives only in Tokonomy's process.
- **Worktree only.** With Antigravity, file tools are scoped to the worktree, temp reads and writes are denied, and shell commands are refused.
- **No tampering.** Creating links or junctions, rewriting the `.git` pointer, replacing managed files, or writing hidden files in ignored paths is a **workspace violation**: the attempt fails at once, is never retried and never handed to the architect.
- **Cannot edit acceptance tests.** Before every gate run Tokonomy rewrites the architect's test files from the plan and deletes any other file in the acceptance directory. Weakening a test does nothing except leave an `acceptance_tamper` record.
- **Cannot touch policy.** `.tokonomy/**` is always a protected path.

### 4.4 Cost of the cheap model

For OpenAI-compatible workers (OpenCode) the **metering proxy** records every upstream call's tokens and cost, independent of what the agent reports, with up to 4 retries on 429/503/529. Failed calls are not recorded. For subscription CLIs (Claude Code, Antigravity, Codex) the usage is parsed from the CLI's JSON output and priced from `prices.yaml`; it is stored as **estimated**, because a subscription is not billed per token. Cache reads are tracked separately from fresh input.

---

## 5. The Supervisor: the decision engine (no LLM)

After every worker attempt, `Supervisor.Decide` applies rules **in this exact order; the first match wins**:

| # | Condition | Decision | Notes |
|---|---|---|---|
| 1 | Worker used the wrong model | **Fail** | Never retried or taken over: a configuration problem |
| 2 | Workspace violation (links, `.git` tampering) | **Fail** | Nothing from that attempt is trusted |
| 3 | Diff larger than `max_diff_lines` (default 400) **and not verified** by a passing gate | **Split** | In practice fails (splitting is not built). A large diff that every gate accepted is **kept** |
| 4 | Diff touches `.tokonomy/**` or a `protected_paths` glob | **Takeover** | |
| 5 | A new dependency was added and `review_on_new_dependency` (default on) | **Takeover**, but this needs a human | Packages listed in `allowed_dependencies` are exempt |
| 6 | Worker timed out (`worker_timeout_seconds`, default 1200) | **Takeover** | |
| 7 | Worker run faulted (crash, error status, denied tool call) | **Retry**, then **Takeover** at the limit | A faulted run is **never accepted**, even if gates pass |
| 8 | No changes made | **Retry**, then **Takeover** at the limit | |
| 9 | A gate has new failures | **Retry** with gate feedback, then **Takeover** at the limit | Limit is `max_retries_per_unit` (default 2) |
| 10 | Otherwise | **Accept** | The unit is committed |

Afterwards the orchestrator applies strategy limits (`Constrain`): with no architect (strategies 1, 1b, 2, 3), Takeover and Split become **Fail**; strategies without gates ignore the size limit; single-shot strategies cap attempts at 1. A takeover only runs if `max_takeovers_per_unit` has not been reached.

### The unit loop as a state machine

```
attempt = 1
loop:
   write brief (+ feedback on retries)  →  run cheap worker  →  inspect workspace
   → diff vs. unit start  →  run gates (sandbox)  →  Supervisor.Decide
   Accept   → commit "tokonomy(U1): <title>"            → next unit
   Retry    → feedback = gate output                    → attempt++ → loop
   Takeover → architect edits → full gates again
                 pass → lock + handoff + commit "[architect takeover]" → next unit
                 fail → reset worktree → unit FAILED → run FAILED
   Fail     → reset worktree → unit FAILED → run FAILED
```

A failed unit stops the whole run (`Fail`, or `AbortedBudget` if the budget ran out). The branch keeps whatever earlier units committed.

---

## 6. Gates and isolation

### 6.1 What a gate is

An automatic pass/fail **command** (an argument array, never a shell string) such as `dotnet build`, `ruff check .`, `pytest -q` or `npm test`. `tokonomy init` detects your stack and proposes gates. Gate kinds:

- `command`: runs a command; may point at a TRX/JUnit report so failing **test ids** can be compared with the baseline.
- `locked_regions`: the built-in check that architect-written text is still in place. It runs on the host and executes nothing.
- **Acceptance tests**: architect-written tests for projects without a suite (landing pages, scripts). They have **no baseline**: any failure blocks the unit. Unit N's gate runs the tests of units 1..N, so a test may only assert what that unit and earlier ones deliver.

### 6.2 Where gates run: Windows Sandbox

Gates run code the worker wrote, so by default they run in a **disposable Windows Sandbox VM**: a copy of the source, **no network**, no access to your files, no clipboard, GPU, audio or printer. There is **no default and no fallback**: a policy with gates but no `gate_execution` section, an unknown provider, or an unavailable sandbox stops the run before any worker starts.

Per gate batch (baseline, every attempt, every takeover check):
1. The host copies only the files git lists, through `WorktreeGuard` (no links, no hard-link tricks, no `.git`, no `.tokonomy`, size-capped).
2. A VM starts with read-only mappings (`src`, `ctl`, `deps`, toolchains) and an empty writable `out`.
3. Inside, `run.ps1` unpacks dependencies, copies the source to `C:\work`, and runs each gate with a fixed environment and a tree-killed timeout.
4. The host validates `out` (no links, fixed names only, size caps, matching nonce), stops the VM, confirms with `wsb list` that it is gone, and deletes the folders without following links.

A link or oversized output raises a security error and is treated as a workspace violation. A provisioning failure is a run **error**, not the worker's fault. `gate_execution.provider: host-unsafe` exists for development and trusted tasks and prints a loud warning. The sandbox was live-verified 16/16 on 2026-11-10; the dev machine's sandbox has no DHCP, so gates run with network `none` there.

---

## 7. The five strategies

A *strategy* is a way of getting a task done. `tokonomy build` always uses **4**. The others exist mainly for the benchmark (`plan --strategy <id>` also accepts them).

| Id | Name | Uses plan | Gates | Takeover | Max attempts | Who writes code |
|---|---|:---:|:---:|:---:|:---:|---|
| **1** | Strong model only, single shot | no | no | no | 1 | strong |
| **1b** | Strong model + gates + retry | no | yes | no | unlimited (policy) | strong |
| **2** | Cheap model only, single shot | no | no | no | 1 | cheap |
| **3** | Architect plan + cheap worker, no gates | yes | no | no | 1 | cheap |
| **4** | Architect plan + cheap worker + gates + supervision | yes | yes | **yes** | policy | cheap (strong rescues) |

Strategies without a plan use the task text as one single unit. The key comparisons are **4 vs 1b** (the value of the split itself) and **4 vs 1** (overall).

---

## 8. Cost tracking and safety nets

- **Ledger** (`.tokonomy/ledger.db`, SQLite): every LLM call (role, model, input/output/cached tokens, USD, metered vs estimated), every attempt (executor, model, exit code, duration, diff size, decision, reason), every gate result and log.
- **Costs are labelled.** `$0.0123` is metered; `$0.0123 (est.)` is an estimate (subscription CLIs, unpriced models).
- **Live cost.** During a run the spinner line shows running cost and tokens (the `IRunObserver.Usage` event).
- **Budget guard.** `budget.max_usd_per_task` (default $3.00) is a hard abort (`AbortedBudget`); planning cost already counts against it. `max_usd_per_run` and `max_architect_token_share` (0.30) are optional/advisory.
- **Redaction.** Gate output goes back to a model, so credential-shaped strings are redacted first (a mitigation, not a guarantee).
- **Ctrl+C is graceful.** First press stops worker process trees, stops sandboxes, and records the run. A second press, or being stuck for about 45 seconds, forces exit with code 130. Nothing is left running.
- **Secrets.** API keys are kept only as user environment variables, never in project files.
- **Proxy.** One per-user HTTP/SOCKS5 proxy covers every model call, login, git and gate command (section 10, *Proxy*).

---

## 9. Project layout

```
Tokonomy.slnx
src/
  Tokonomy.Core      pure logic, no I/O: Supervisor, Policy, contract model + validator, edit blocks, handoff, proxy settings, SOCKS5 bridge
  Tokonomy.Llm       architect side: Planner, TakeoverPrompt, ReviewPrompt, OpenAI-compatible provider, price table, metering proxy
  Tokonomy.Runtime   the engine: RunOrchestrator, executors, gates + Windows Sandbox, git worktrees (WorktreeGuard),
                     ledger (SQLite), setup/scaffolding, CLI-backed providers (Claude/Agy/Codex), process handling
  Tokonomy.Bench     benchmark harness: taskset loader, BenchRunner, BenchReport
  Tokonomy.Cli       Spectre.Console CLI + interactive Shell: commands, setup wizard, TUI
tests/
  Tokonomy.Core.Tests  Tokonomy.Runtime.Tests  Tokonomy.E2E.Tests      (about 490 test methods)
examples/            tasksets (tasks, tasks-hard, tasks-extreme), fixtures (login-demo, shop-demo),
                     hidden acceptance tests, reference solutions, verify_tasks.py
docs/                config, benchmarking, glossary, threat-model, gate-isolation (+ research)
install/             install.sh, install.ps1 (SHA-256 verified single-file binaries)
```

Per-project files created by `tokonomy init` in `.tokonomy/`: `policy.yaml`, `executors.yaml`, `prices.yaml` (commit these); `ledger.db`, `runs/`, `tmp/` (do not commit). Per-user files live under `%LOCALAPPDATA%\tokonomy\` (proxy settings, remembered defaults, worktrees, gate environments).

---

## 10. Every section of the menu

Run `tokonomy` with no arguments in a terminal (or `tokonomy shell`) to open the home screen: a banner and an arrow-key menu (up/down to move, Enter to select, wraps around). In a pipe or CI, `tokonomy` with no arguments prints help instead. Each entry runs the matching normal command, so the menu and the command line share one code path. After a command finishes, "Press any key to go back to the menu" appears.

The menu has **11 entries plus Quit**:

### 1. Type a task or /command
One prompt, like Claude Code's. Plain text is treated as a task and runs `build`. Text starting with `/` runs a command:

| You type | Runs |
|---|---|
| any plain text | `build "<text>"` |
| `/build <task>` | `build` |
| `/plan <task>` | `plan` |
| `/doctor` | `doctor` |
| `/report` or `/runs` | `report` |
| `/run` | `run` |
| `/setup` or `/init` | `init` |
| `/login` | `login` |
| `/proxy` | `proxy` |

Extra words after a slash command are passed as arguments (for example `/login codex`). `/build` and `/plan` without a task show a warning. An unknown command shows a hint. Pasted multi-line tasks are kept whole (commit `7330893`).

### 2. Build a task
Prompts "Describe the task", then runs `build`: setup if needed, preflight, plan, confirm, build and verify, offer merge. Flags on the CLI: `--yes`, `--merge`, `--keep-worktree`.

### 3. Plan only
Prompts for a task and runs `plan`. The architect writes the contract and the plan is saved, but nothing is built. Execute later with Run.

### 4. Run the latest plan
Runs `run`, which executes the most recent plan still in `Planned` state. CLI extras: `--plan <id>` to choose one; `--again` to run an already-executed plan as a **new** run (a fresh branch, worktree and ledger rows, no new planning cost, so it suits a failed run); `--keep-worktree`.

### 5. Runs and costs
Lists the last 20 runs, each as `id  date  status  cost  task`. Pick one to open `report <id>`: the status, then a table per **tier** (architect vs worker: calls, input tokens, output tokens, cost), the total, and the **architect token share**. Estimated figures are marked `(est.)`. CLI: `report [run-id] [--limit N] [--json]`.

### 6. Doctor
Submenu:
- **Check everything:** git, each executor and its tested version, capabilities, API keys and logins, gate tools, prices, Windows long-paths, sandbox availability.
- **Check and remove leftover worktrees:** cleans worktrees left by crashed runs.
- **Check and remove leftover gate sandboxes:** destroys sandbox environments left behind.

### 7. Setup
Submenu:
- **Guided setup (recommended):** `init`.
- **Redo setup and overwrite the current one:** `init --force`.
- **Use the same choices as last time, no questions:** `init --yes`.
- **Change where tests run (safe sandbox or this computer):** opens the isolation screen, which explains the gates, shows whether Windows Sandbox is installed, and switches `gate_execution.provider` between `windows-sandbox` and `host-unsafe` in `policy.yaml`.

Guided setup first scans the machine (is Claude Code installed and signed in? Antigravity? Codex? OpenCode? Python? Windows Sandbox?), then asks two **independent** questions: who is the **strong** model (plans, rescues, writes tests) and who is the **cheap** model (writes code). Each can come from Claude (subscription or API key), Gemini (Antigravity subscription or API key), GPT (Codex or an API key), OpenRouter (about 20 models with live prices and at least two free ones), DeepSeek, or a **custom** OpenAI-compatible endpoint. Keys are pasted once, validated, and stored only as a user environment variable. Missing pieces are named with the exact fix command, and setup can be finished later. Non-interactive form: `tokonomy init --yes --strong claude --cheap antigravity`. The cheap role always needs a coding agent (Claude Code, Antigravity, Codex or OpenCode).

### 8. Accounts
Submenu for subscription logins: **Show sign-in status**, **Sign in to Claude**, **Sign in to Codex (browser)**, **Sign in to Codex (device code)** (for machines without a browser), **Sign in to Antigravity**. It runs each tool's own login with the terminal handed over, with the proxy applied. CLI: `login [claude|codex|antigravity] [--device-code]`.

### 9. Proxy
Submenu: **Show current proxy**, **Set a proxy address** (asks for it, for example `127.0.0.1:10808`), **Choose which connections use it** (scope: everything, only the strong model, only the cheap model, or per provider), **Turn on**, **Turn off** (address kept), **Test connections** (reachability and speed of the proxy and of each model or subscription you use; `--live` adds a tiny real call per model). It supports HTTP, HTTPS and SOCKS5; since Claude Code and most tools cannot speak SOCKS, Tokonomy runs a local HTTP bridge that tunnels through your SOCKS proxy. Settings are per user (`%LOCALAPPDATA%\tokonomy\proxy.json`); `TOKONOMY_PROXY` overrides; localhost is never proxied.

### 10. Benchmark
A wizard that avoids accidental spending:
1. Pick a taskset (it lists `tasks*.yaml` files in the current folder, or type a path).
2. Tick the strategies to compare (default ticked: 1, 1b, 2).
3. Trials per task (1 to 10).
4. **Hard spend cap in USD** (required, default $0.50).
5. "Dry run first?" (default yes): prints the trial matrix and budget ceiling with no model calls.

It runs `bench <file> --strategies ... --repeat N --max-usd X [--dry-run]`. Each trial runs in a fresh clone, success is judged only by **hidden acceptance tests** the worker never sees, and the report is saved to `.tokonomy/runs/bench-<id>.md`. Each trial may spend at most the smaller of the per-task budget and what is left, and no new trial starts once less than $0.01 remains.

### 11. Help
A table of terms: Plan, Unit, Gate, Retry, Ledger, Takeover, Strategy, Isolation, with a pointer to `docs/glossary.md`.

### Quit
Exits ("Bye.").

### Commands that exist but are not separate menu entries
`tokonomy init`, `login`, `proxy set|scope|on|off|test`, `bench`, `report`, `doctor`, `plan`, `run`, `build`, `shell`. All accept `--config <dir>`, and most accept `--json`.

---

## 11. What we did, in order

From the 31 commits:

| Stage | What was built |
|---|---|
| **M0** | CLI skeleton, config, ledger, provider, release pipeline |
| **M1** | Supervised runs, metering proxy, planner, benchmark harness |
| Verification | OpenCode adapter verified, AvalAI path, tiered pricing, 429 retry |
| Hardening of the benchmark | Spending cap, harder verified taskset on the `shop-demo` fixture, guard against unpriced models, `--worker`/`--strong` overrides |
| **M2** | **Takeover flow** with handoff and locked regions; relicensed under MIT |
| Rename | Taskrail, then Tokonomy; docs and install URLs updated |
| Strong model | `claude-sonnet-5-5` as the benchmark strong model |
| Subscription adapters | `claude-code-sub`, Claude Code JSON usage; claude-cli architect provider, Antigravity usage parsing and adapter |
| Extreme taskset | Six hard algorithmic tasks with hidden tests and references |
| **Phase 2.5** | Windows Sandbox gate isolation, worktree and worker hardening (links, `.git` tampering, credential scrubbing) |
| Guided setup | Any-provider connections, architect-written acceptance tests, a Claude + Gemini benchmark pilot |
| Docs | Gate explanation, glossary, threat model, benchmark results |
| Proxy | Per-user HTTP/SOCKS5 proxy covering all calls, child processes and a connection test |
| Providers | Codex and custom providers, sign-in flow, proxy scope |
| Interactive shell | Banner, arrow-key menu with a submenu for every section |
| UX | Live cost and tokens during a run; typed task / slash-command prompt; benchmark wizard; Setup submenu and shell tests |
| Setup option | "Change where tests run" |
| Planning | The architect can ask clarifying questions before planning |
| Reliability | Pasted tasks kept whole; Ctrl+C leaves nothing running; test runs no longer overwrite the user's saved setup |
| Streaming | Every provider streams with live progress, stall detection and one retry |
| Dependencies | `escalation.allowed_dependencies` to pre-approve packages |
| Reruns | `run --plan <id> --again` |

### Benchmark results so far (small, not conclusive)

- **API run** (8 tasks, Sonnet 5.5 as strong, DeepSeek V4.1 Flash as cheap via OpenCode, 2026-11-09, `--repeat 3 --max-usd 4.50`): strategy 4 passed 21/21 at about **$0.047 per success**; strategy 1b passed 23/23 at **$0.153**. That is roughly 3.3x cheaper at the same pass rate. But the cheap worker never failed, so takeover was never exercised, and 4 trials were skipped by the cap.
- **Subscription pilot** (Opus plans and supervises, Gemini 3.8 Flash high writes code, 2 tasks, 2026-11-10): all strategies passed 2/2. Opus alone was fastest (1.8 min) and cheapest in API-equivalent terms ($0.37 vs about $1.40). What the split bought was **Claude quota**: about 16k Claude tokens versus 57k (+518k cache reads) when Opus did the work. A harness bias was found and fixed: strategies without gates were discarding output over 400 diff lines. Older results for strategies 1, 2 and 3 are not comparable.
- **Honest conclusion so far:** the direction looks right on cost, but the tasks were too easy to prove supervision pays off. Still needed: harder tasks where the cheap worker fails, more than one repo and language, a second executor, and more repeats.

---

## 12. Known gaps and inconsistencies

1. **Splitting oversized units is not implemented.** `Split` always ends in `Fail`.
2. **Protected-path and new-dependency escalations need a human.** The supervisor labels them `Takeover`, but the orchestrator turns them into a `Fail` with an explanation unless the trigger allows takeover.
3. **Benchmark wizard labels do not match the real strategies.** `Shell.cs` calls strategy 2 "cheap model alone + gates" and strategy 3 "cheap model with strong-model review". In `Strategies.cs` strategy 2 is cheap alone with **no gates**, single shot, and strategy 3 is plan + cheap worker with **no gates**. The code and `docs/benchmarking.md` agree with each other; the wizard text is wrong.
4. **`docs/config.md` supervisor table is out of date.** It lists 7 rules; the code has 10 (it adds the wrong-model, workspace-violation and faulted-run rules, and the "large diff kept if verified" exception).
5. **README is older than the code in places.** It still says the OpenCode adapter is the only verified one and that Claude Code, Codex and Gemini are "planned"; adapters for all three exist now, though Codex "has not yet been run end to end on a signed-in account".
6. **Only OpenRouter and the CLI logins have been run live.** Anthropic, Gemini, OpenAI, DeepSeek and custom endpoints are marked "not tested live".
7. **Windows-first.** Gate isolation needs Windows Sandbox. Other platforms need `host-unsafe`, which gives up gate isolation.
8. **The worker CLI itself is not sandboxed by Tokonomy.** It runs with your privileges under its own permission rules; only the gates are VM-isolated.
9. **Pre-alpha.** The core hypothesis is still being tested.

---

## 13. Quick reference

```sh
tokonomy                                     # interactive menu
tokonomy build "a landing page"              # setup + plan + confirm + build + verify + merge
tokonomy init                                # guided setup (--force to redo, --yes to reuse)
tokonomy doctor                              # check everything
tokonomy plan "Add rate limiting to /login"  # plan only (--strategy 1|1b|2|3|4, --no-questions)
tokonomy run                                 # run the latest plan (--plan <id>, --again, --keep-worktree)
tokonomy report [run-id]                     # cost per tier, tokens, architect share
tokonomy login [claude|codex|antigravity]    # sign in to a subscription
tokonomy proxy set 127.0.0.1:10808           # one proxy for everything (also: scope, on, off, test --live)
tokonomy bench examples/tasks.yaml --strategies 1,1b,2,3,4 --repeat 3 --max-usd 3.00 [--dry-run]
git merge tokonomy/<run-id>                  # bring a passed run into your branch
```

Build and test from source: `dotnet build`, `dotnet test`, `dotnet run --project src/Tokonomy.Cli -- --help` (needs the .NET 10 SDK).
