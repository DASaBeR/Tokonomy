# Tokonomy — Project Brief v2

> **Make AI coding cheaper. Keep engineering quality high.**
>
> An open-source CLI that coordinates coding agents, routes work across models, enforces deterministic quality gates, and measures the real cost of successful software delivery.

**Status:** Draft v2 (design phase)
**Language:** C# / .NET
**License:** MIT or Apache-2.0
**Name:** "Tokonomy" is a working name. Before committing, check GitHub, NuGet, npm, domains, and trademarks. Do the same for any alternative name.

### What changed from v1

| Change | Reason |
|---|---|
| Primary metric is now **cost per successful task** | A cheap run that fails is not a saving |
| Four-strategy benchmark, started in M1 | Prove the premise early; isolate whether the supervisor earns its cost |
| Executor **capability declarations** | CLIs differ in model override, structured output, session resume, token reporting |
| Security boundaries moved from v2 to v1 | Git worktrees are not sandboxes |
| Takeover uses **structured edits**, not a built-in agent | Removes the riskiest component |
| Supervisor starts with three outcomes | Fewer rules, more predictable, easier to benchmark |
| MCP, parallel execution, NativeAOT, extra adapters postponed | Validate the core hypothesis first |

---

## 1. Problem

Developers use expensive models for routine edits. Cheap models are good enough for much of the work but fail on hard sections and drift from architecture standards. No neutral, open tool coordinates multiple agents and providers with deterministic quality gates and an honest cost record.

## 2. Hypothesis

> A strong model used sparingly (planning, takeover, final review) plus a cheap model for execution, verified by deterministic gates, completes tasks at comparable quality for significantly lower **cost per successful task** than a strong model alone.

This is a hypothesis, not a fact. The project's first job is to test it. See the kill criterion in section 13.

## 3. Goals and non-goals

**Goals**

- Lower cost per successful task with no meaningful quality drop.
- Work with any CLI coding agent and any OpenAI-compatible provider.
- Be deterministic and auditable: gates and policy decide, not LLM opinion.
- Make cost and quality measurable on the user's own repos.
- Ship as a single cross-platform binary with a one-command install.

**Non-goals (v1)**

- Not a new IDE, chat UI, or general multi-agent framework.
- Not a new coding agent.
- No hosted service. No telemetry by default.
- No parallel execution, MCP server, or container sandbox yet.

## 4. Design principles

1. **LLMs produce content; policy makes decisions.** Plans, guidance, and patches come from models. Accept, retry, and takeover decisions come from deterministic rules.
2. **Gates outrank opinions.** Tests, linters, and architecture rules carry more weight than any model's review.
3. **Judge by the diff, not the report.** The orchestrator computes diffs from git and never trusts an agent's self-report.
4. **Front-load the expensive model.** The architect writes a contract once, then acts only on triggers.
5. **Fresh context after intervention.** Never continue a worker session polluted by a bad approach.
6. **Measure everything.** If it is not in the ledger, it did not happen.
7. **Start simple.** Add escalation levels only when benchmark data shows they help.

## 5. Core concepts

| Concept | Definition |
|---|---|
| **Provider** | An LLM endpoint (OpenRouter, DeepSeek, Gemini, Anthropic, OpenAI, local). Used for architect calls and metering. |
| **Executor** | An adapter around a coding-agent CLI (Claude Code, Gemini CLI, Codex, OpenCode). Treated as a black-box subprocess. |
| **Architect** | Strong model: writes the contract, handles takeovers, does the final review. |
| **Worker** | Cheap model run through an executor. |
| **Contract** | Units, dependency DAG, rules, acceptance checks, and a "don't" list. |
| **Gate** | A deterministic check: build, lint, types, tests, architecture rules, locked-region check. |
| **Supervisor** | Policy-driven decision logic: accept, retry, or takeover. |
| **Handoff** | Structured file describing architect changes, locked regions, and the resume point. |
| **Ledger** | Per-task record of cost, tokens, attempts, escalations, and outcome. |

**Providers and executors are separate axes.** A coding CLI and an LLM API are different integrations. Some executors let you choose the model, some are tied to one vendor, and some can be redirected through a gateway. The orchestrator calls the architect through the provider layer and calls workers through executor adapters with an optional model override.

## 6. Workflow

```
Task
  ↓
PLAN       Architect → contract + unit DAG + gate rules
  ↓
PREPARE    Worktree per unit; brief written to the agent's native file
  ↓
EXECUTE    Worker (cheap) runs the unit via an executor
  ↓
GATE       Build, lint, types, tests, arch rules, locked regions
  ↓
DECIDE     Supervisor applies policy (a faulted worker run is never accepted)
  ├── Accept   → merge unit
  ├── Retry    → rerun worker with gate or fault feedback (up to N)
  ├── Takeover → architect applies structured edits → handoff → fresh worker
  └── Fail     → wrong model: never retried, never taken over
  ↓
FINAL      One advisory architect review of the combined diff (opt-in; cannot change the status)
  ↓
LEDGER     Cost, quality, escalations, baseline comparison
```

### Escalation ladder (v1)

1. **Retry with feedback.** Gate output goes back to the worker. This costs nothing in architect tokens.
2. **Takeover.** After N failed retries, or when a trigger fires, the architect fixes only the smallest failing section, then hands back.

*Guidance* (a short architect correction before retry) and *full reassign* are v1.1 candidates. Add them only if benchmark data shows they help.

### Takeover triggers (deterministic)

- A gate failed N times on the same unit (default 2).
- The diff touches a protected path (public API, schema, auth, config).
- The diff exceeds the size limit (default 400 lines).
- A new dependency was added.
- The worker looped or timed out.

### Takeover mechanics

The architect does **not** run as a tool-using agent. It receives the contract, the unit spec, the diff, and the failure output (the minimum context), and returns **structured edits** (search/replace blocks or a unified diff) for the failing section only. The orchestrator applies the edits, formats the code, and runs the gates.

### Handoff and locked regions

After a takeover the orchestrator writes `handoff.yaml`:

```yaml
done_by_architect:
  files: [src/auth/token.py]
  summary: "Implemented refresh-token rotation with dependency injection"
  why: "Worker used module-level state, violating rule R1"
locked:
  - path: src/auth/token.py
    symbol: rotate_token        # a gate fails if these lines change
interfaces:
  - "rotate_token(user_id: str) -> Token"
worker_next:
  resume_from: "U3: wire rotate_token into /refresh"
  must_follow: ["use dependency injection", "no module-level state"]
  acceptance: ["tests/test_refresh.py passes"]
```

- A **locked-region gate** fails the run if protected lines change. Enforcement is deterministic, not a polite request.
- The next worker starts a **fresh session** with the contract, the handoff, and only the relevant files.
- After handback the **full gate suite** re-runs, not just the new unit's tests, because the architect's change may break assumptions elsewhere.
- Each takeover can append a short rule to a project `lessons` file, ideally as a semgrep or lint rule, so the same mistake is caught earlier next time.

## 7. Example

**Task:** "Add rate limiting to `/login` in a Flask app."

```
PLAN     contract: U1 RateLimiter (injectable store), U2 apply to /login (depends on U1)
         rules: R1 no module-level mutable state (semgrep), R2 no new deps
U1       worker attempt 1: ruff ✓ mypy ✓ pytest ✓ semgrep R1 ✗
U1       worker attempt 2 (gate feedback attached): R1 ✗
U1       TRIGGER: gate failed twice → takeover
         architect fixes store handling only; locks RateLimiter; writes handoff
U2       fresh worker session: all gates ✓, locked-region check ✓
FINAL    architect reviews combined diff → approve
```

Illustrative ledger shape (numbers are placeholders, not measurements):

```
Task: rate-limit-login          Status: PASS
Tier        Calls   Tokens    Cost
architect   3       ...       ...
worker      5       ...       ...
Takeovers: 1/2 units   Retries: 2
Cost per successful task: ...   vs strong-only baseline: ...
```

## 8. Architecture (C#)

| Area | Choice |
|---|---|
| Runtime | .NET (current LTS); self-contained single-file publish, NativeAOT later if needed |
| CLI | Spectre.Console.Cli or System.CommandLine |
| LLM access | `Microsoft.Extensions.AI` over OpenAI-compatible HTTP; OpenRouter as the universal route; optional LiteLLM proxy |
| Processes | `System.Diagnostics.Process` with timeouts, output capture, and process-tree kill |
| Git | Shell out to `git` for worktrees (more reliable than LibGit2Sharp for worktree features) |
| Config | YAML (YamlDotNet) with schema validation |
| Storage | SQLite for the ledger; JSONL for transcripts |
| Tests | xUnit with a fake executor and fake provider |

### Key interfaces

```csharp
public interface IExecutor {
    string Name { get; }
    ExecutorCapabilities Capabilities { get; }
    Task<RunResult> RunAsync(Brief brief, string workdir,
                             string? model, TimeSpan timeout, CancellationToken ct);
}

public interface IGate {
    Task<GateResult> RunAsync(string workdir, Contract contract, CancellationToken ct);
}

public interface ISupervisor {
    Decision Decide(UnitState unit, RunResult run,
                    IReadOnlyList<GateResult> gates, Policy policy);
}
```

### Executor capability declarations

Each executor declares what it supports, and the supervisor adapts instead of assuming. `doctor` verifies the declarations.

```yaml
executors:
  opencode:
    cmd: ["opencode", "run", "{prompt}"]     # verify flags against current docs
    brief_file: AGENTS.md
    capabilities:
      headless: true
      model_override: true
      structured_output: false
      session_resume: false
      reports_tokens: false      # ledger marks cost as "estimated"
      permission_modes: true
```

Agent CLI flags change often. Keep adapters as configuration, pin tested versions, and accept community PRs.

### Implemented: Claude architect + Antigravity worker (subscriptions, no API key)

```
                     ┌──────────── architect_provider.kind = claude-cli ────────────┐
 plan / takeover /   │ claude -p  (stdin prompt, --tools "", no MCP, no settings)    │
 final review ──────►│ requested model must be among the models that produced output │
                     └───────────────────────────────────────────────────────────────┘
                     ┌──────────── executor "antigravity" (agy, headless) ──────────┐
 work unit ─────────►│ --output-format stream-json --settings <per-attempt file>     │
                     │ permissions: read/write_file(worktree), no commands (default) │
                     │ deny: .git, .tokonomy, escaping links, ignored, temp, logins  │
                     └───────────────┬───────────────────────────────────────────────┘
                                     ▼
              AgyRunParser → AgyRunInterpreter → RunResult { Fault?, ReportedModel, Audit }
                                     ▼
        WorkspaceWatch: new links / .git tampering / hidden ignored files ⇒ fault
                                     ▼
   Supervisor: Fault ⇒ never Accept (Retry → Takeover; ModelMismatch, WorkspaceViolation ⇒ Fail)
```

- **Providers and executors stay separate axes.** The architect runs through the provider layer (`IProvider`); the worker runs through an executor adapter. Neither can fall back to the other: the worker's model is checked against the session's reported model, the architect's against the models that produced output.
- **Faults are typed** (`WorkerFault`: `ProcessFailed`, `ReportedError`, `PermissionDenied`, `ModelMismatch`, `NoResult`) and carried on `RunResult` next to the reported model and an `ExecutorAudit` (conversation id, shell commands run, denied actions, isolation notes). The supervisor checks the fault before the diff or the gates, so an attempt that crashed or was refused can never be accepted because its partial diff happens to pass.
- **Executable discovery.** `ExecutableResolver` tries `PATH` and then known install directories (`~/.gemini/bin` for `agy`), also inside a scrubbed worker environment.
- **Permissions are generated per attempt** from the gate commands and `permissions` config, written outside the worktree, and described to the worker in its brief. Rule semantics and every verified limitation are in [docs/threat-model.md](docs/threat-model.md) and [docs/config.md](docs/config.md).
- **Worktree location** defaults to the local application data folder (outside temp and the repository) and is validated before use; see "Worktree security" below.
- **Final review** (`escalation.final_review`) is one architect call over the combined diff after all units pass. It is advisory and recorded; gates outrank opinions.

### Worktree security

A worker is untrusted code with the user's privileges, so the worktree boundary is enforced in three places:

1. **The agent's own permissions** (agy settings generated per attempt): worktree-only file access, no shell, deny rules for `.git`, `.tokonomy`, links that lead outside, ignored content, temp and credential folders.
2. **Tokonomy's own file access** (`WorktreeGuard`): every read/write/delete Tokonomy performs inside a worktree refuses links on the path and, on Windows, opens the target as a reparse point and re-checks the handle (hard links, final path). Worktree git commands use the git dir recorded at creation, never the worktree's `.git` file, and run without inherited `GIT_*` variables.
3. **Post-attempt inspection** (`WorkspaceWatch`): links are baselined after `prepare` and the base-commit gates; new escaping links, retargeted links and `.git` tampering fail the unit (`WorkspaceViolation`, never retried, never taken over, gates skipped). Cleanup unlinks before `git worktree remove`, which otherwise follows junctions and deletes their targets.

Worktrees live under `%LOCALAPPDATA%\tokonomy\worktrees` (platform equivalents elsewhere); a configured root is refused if it overlaps temp, the repository, home, a drive root or system/credential folders. Gates execute code the worker wrote; with `gate_execution.provider: windows-sandbox` they run in a disposable Hyper-V-isolated VM per batch (source copy, no network, no host credentials, validated results), and there is no silent fallback to the host (threat model T16, G1-G13; `docs/gate-isolation-research.md`).

Verification is layered: unit tests for the parser, interpreter, settings builder and supervisor (fixtures captured from real `agy` and `claude` output); orchestrator tests with a scripted worker and architect over real git, gates and ledger; and a gated live suite (`TOKONOMY_LIVE=1`) that runs the real CLIs end to end (see `LiveIntegrationTests.cs`).

### Policy file

```yaml
models:
  architect: openrouter/<strong-model>
  worker:    <cheap-model>

executors:
  default: opencode

gates:                                  # argument arrays, never shell strings
  - { cmd: ["ruff", "check", "."] }
  - { cmd: ["pytest", "-q"] }
  - { type: locked_regions }

escalation:
  max_retries_per_unit: 2
  protected_paths: ["src/auth/**", "migrations/**"]
  max_diff_lines: 400
  review_on_new_dependency: true

budget:
  max_usd_per_task: 3.00                # hard abort
  max_architect_token_share: 0.30       # exceeding this signals a policy problem
```

## 9. Security

Git worktrees isolate changes, not execution. Workers can run arbitrary commands, so v1 includes:

- **A written threat model** (`docs/threat-model.md`) before the first executor ships.
- **Scrubbed environment:** workers receive only an allowlisted set of environment variables. Provider API keys never enter worker processes.
- **Agent permission modes:** use each agent's own approval and permission settings; the adapter declares which it supports.
- **Gates run with no network** unless a gate declares a need.
- **No secrets in briefs, transcripts, or the ledger.**
- **Budget hard cap** per task and per run.
- **Documented limits:** state plainly that worktrees are not a sandbox. Container sandboxing is on the roadmap.
- **Worktree boundary (implemented):** worktrees outside temp and the repository; no link-following in Tokonomy's own file access or cleanup; per-attempt detection of links and `.git` tampering; the worker's agent denied links, git metadata, temp and credential folders. **Gate isolation (implemented, Phase 2.5):** gates, prepare steps and bench acceptance run through an `IGateExecutionProvider`: `windows-sandbox` (default when scaffolded) or an explicit `host-unsafe`; see `docs/gate-isolation.md`.

## 10. CLI surface (v1)

```
tokonomy init            # detect stack, scaffold policy.yaml and executors.yaml
tokonomy doctor          # check git, executors, capabilities, API keys, gate tools
tokonomy plan "<task>"   # architect produces contract + unit DAG
tokonomy run             # execute units under supervision
tokonomy report          # cost and quality ledger
tokonomy bench <taskset> # compare strategies (see section 11)
```

`init` auto-detects the project stack and writes default gates:

| Detected | Default gates |
|---|---|
| `*.sln` / `*.csproj` | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` |
| `package.json` | `npm test`, `eslint`, `tsc --noEmit` |
| `pyproject.toml` | `ruff check`, `mypy`, `pytest` |
| `go.mod` | `go vet`, `go test ./...` |
| `Cargo.toml` | `cargo clippy`, `cargo test` |
| `pom.xml` / `build.gradle` | `mvn test` / `gradle test` |

Semgrep is a useful cross-language option for architecture-rule gates.

## 11. Benchmarking and metrics

### Primary metric

> **Cost per successful task** = total spend across all attempts, retries, takeovers, and reviews ÷ number of tasks that pass acceptance.

### Four strategies

| # | Strategy | Question it answers |
|---|---|---|
| 1 | Strong model only | The quality and cost baseline |
| 2 | Cheap model only | How far cheap gets alone |
| 3 | Architect + worker (no supervision) | Does the split help by itself? |
| 4 | Architect + worker + deterministic supervision | Does the supervisor earn its complexity? |

### Method

- Start the harness in **M1**: a task list, a runner, and a cost-per-success table.
- Use your own repos and a public set (for example, a SWE-bench subset).
- **Repeat each task 3 or more times** and report ranges. Model output is non-deterministic.
- Claim only what you measured, for example "on these N tasks in these repos."
- Mark costs from agents that do not report usage as **estimated**.

### Targets (to be validated, not assumed)

| Metric | Target |
|---|---|
| Cost per successful task vs strong-only | ≥ 40% lower |
| Pass-rate difference vs strong-only | Within 5 percentage points |
| Architect token share | < 30% |
| Takeover rate | < 25% of units, trending down |
| Human intervention | No meaningful increase |

## 12. Milestones

| Milestone | Scope | Estimate |
|---|---|---|
| **M0** | Repo, CLI skeleton, config, provider client, SQLite ledger, **release pipeline** (7 binaries + install script on day one), threat model | 1 week |
| **M1** | Contract and unit DAG generation, one CLI executor with capabilities file, worktrees, gates, retry, **minimal benchmark harness** | 1–2 weeks |
| **M2** | Takeover via structured edits, handoff, locked-region gate, protected-path and size triggers | 1–2 weeks |
| **M3** | Full four-strategy benchmark, first public write-up, second executor adapter | 1–2 weeks |
| **Decision point** | Apply the kill criterion | — |
| **M4** | Final review, polish, docs, `doctor`, release 0.1 | 1–2 weeks |
| **Later** | Guidance step, MCP `ask_architect`, parallel units, container sandbox, more adapters, NativeAOT, learned routing | — |

## 13. Kill criterion

After M3, if strategy 4 does not beat strategy 1 on **cost per successful task** at comparable pass rate, pivot. Drop the architect premise and ship a **gates + cost ledger + multi-agent runner** tool, which is still useful on its own. Also compare 3 vs 4: if supervision does not improve on the plain split, simplify it.

## 14. Distribution

- Self-contained single-file binaries via a GitHub Actions matrix: `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, `win-arm64`.
- One-command installs: `install.sh` (Linux/macOS, with checksum verification), `install.ps1` (Windows), then Homebrew, Scoop, winget, `dotnet tool`, and a Docker image.
- CI on Linux, macOS, and Windows from day one.
- Use argument arrays (not shell strings) for gates and executors so behavior is the same across shells.
- Test Windows long paths and worktree file locking early.

## 15. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Savings do not materialize | Early benchmark, kill criterion, cost per successful task as the metric |
| Architect overhead exceeds savings | Track architect token share; event-driven review only |
| Vendors build this in | Stay neutral across vendors; lean on gates, ledger, and benchmarks |
| Cheap models close the gap | Gates and cost accounting stay valuable; do not tie identity to one tier split |
| Agent CLIs change flags | Config-based adapters, capability declarations, `doctor`, version pinning |
| Executors hide token usage | Gateway metering where possible; label the rest "estimated" |
| Vendor terms restrict automation | Document per-agent compliance; prefer API-key usage |
| Lossy context handoff | Structured handoff, fresh sessions, locked regions |
| LLM reviewer bias | Gates outrank opinions; review only on triggers |
| Supervisor complexity | Three outcomes at first; deterministic rules; add only with data |
| Small benchmark misleads | Repeat runs, report ranges, scoped claims |
| Scope creep | Strict non-goals and postponed list |

## 16. Open-source setup

- License: MIT or Apache-2.0.
- README leads with the benchmark chart and a 60-second demo (takeover plus ledger).
- `CONTRIBUTING.md` focused on writing executor adapters and gate templates.
- `examples/` with a small sample repo and task.
- `docs/threat-model.md` and `docs/adapters.md`.

## 17. Open questions

1. Which two executors first? Pick ones with solid headless modes on all three OSes.
2. Which public task set best fits the target users?
3. Metering: route worker traffic through a local proxy for uniform cost data, or accept estimates?
4. Edit format for takeovers: search/replace blocks or unified diffs? Test both on real tasks.
5. Final name, after availability checks.

## 18. Definition of success

A developer can say: *"I completed the same engineering task, at comparable quality, for significantly less money,"* and the ledger proves it.
