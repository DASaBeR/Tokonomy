# Adaptive planning and supervision (strategy 5)

Strategy 5 is strategy 4 (architect plan + cheap worker + gates + takeover) with the strong model kept in charge of
the engineering for the whole run, not only at planning time:

- **Plan**: the architect reads the repository, writes an engineering *blueprint* (analysis, architecture, decisions,
  detailed units), and the plan is checked deterministically and critiqued before you see it.
- **Execute**: the worker gets a precise brief compiled from the *current* plan revision, with clear autonomy limits,
  and reports blockers in a structured file instead of guessing.
- **Supervise**: deterministic triggers call the architect to diagnose a problem and pick an intervention level
  (instruction correction, guided correction, takeover). It can revise the remaining plan and record lessons that later
  units inherit. A failed takeover no longer ends the run when recovery is safe.
- **Review**: units whose gates passed are reviewed for architectural compliance when their risk warrants it.

Everything a model produces is validated by code before it has any effect, gates still decide acceptance, and every
architect call is budgeted and recorded. Strategy 5 is opt-in: `tokonomy build` and `tokonomy plan` use strategy 4
by default; select 5 with `tokonomy plan --strategy 5 "<task>"` then `tokonomy run`, or `bench --strategies 4,5`. Whether strategy 5 is *better* is a benchmark question, not a claim (see
[Benchmarking](benchmarking.md)).

## Where each responsibility lives

The logical roles map onto existing components; most new logic is pure and lives in `Tokonomy.Core`.

| Responsibility | Code |
|---|---|
| Repository analysis | `Runtime/Orchestration/RepositoryAnalyzer.cs` |
| Architect planning, file inspection, revision loop | `Llm/Architect/Planner.cs` (prompts), `Llm/Architect/PlanJson.cs` (parsing) |
| Plan critique | `Llm/Architect/PlanCritic.cs` |
| Structural and engineering-quality validation | `Core/Planning/ContractValidator.cs`, `Core/Planning/PlanQuality.cs` |
| Plan revisions, unit progress, lessons | `Core/Planning/PlanManager.cs` |
| Brief compiler | `Runtime/Executors/BriefWriter.cs` (`Compile`) |
| Worker reports | `Core/Supervision/WorkerReport.cs` |
| When to call the architect, which levels are allowed | `Core/Supervision/Adaptive.cs` (`InterventionPolicy`) |
| Diagnosis and intervention prompt | `Llm/Architect/SupervisorPrompt.cs` |
| Carrying out interventions, recovery, decomposition, review | `Runtime/Orchestration/RunOrchestrator.Adaptive.cs` |
| Review policy, plan deviations, lesson enforcement | `Core/Supervision/Adaptive.cs` (`ReviewPolicy`, `PlanDeviation`, `LessonCheck`) |
| Architectural review prompt | `Llm/Architect/UnitReviewPrompt.cs` |
| Persistence | `Runtime/Ledger/SqliteLedger.cs` (schema v3), `runs/<id>/plan-state.json` |

## 1. Planning

### Repository investigation

Before the architect is called, Tokonomy collects facts itself, from the base commit only (through `git`, so nothing
untracked or outside the repository is read):

- an inventory: languages, build and dependency manifests, entry points, test locations, documentation;
- uncommitted changes in your working copy (flagged as *not* part of the base the run starts from);
- the tracked file list;
- the files most relevant to the task (task words matched against paths and, with one `git grep`, contents), with
  their contents, plus manifests and the README, within `planning.max_context_chars`.

The context is marked as collected by Tokonomy, so the architect can tell verified facts from its own guesses. With
`planning.inspect_files` the architect may answer once with `{"read_files": [...]}` to read specific tracked files
before it plans; paths outside the repository are dropped and untracked files are refused.

### The blueprint

With `planning.blueprint: true` (default) the architect must return a schema-version-2 plan:

- **analysis**: facts with evidence paths, assumptions kept separate from facts, requirements with ids, behaviour to
  preserve, risks;
- **architecture**: summary, components (path, create or modify, responsibility), invariants, error handling, security
  and performance notes;
- **decisions**: ADRs (decision, rationale, rejected alternatives, constraints), so the worker never makes them;
- **units**: objective, `files: {create, modify, avoid}`, references to reuse, ordered implementation steps,
  interfaces exactly as they must appear, the ADRs and constraints that apply, edge cases, covered requirements,
  measurable acceptance criteria, verification hints (shown to the worker; Tokonomy never executes them) and a risk.

All new fields are optional on the wire. Plans saved by older builds load unchanged and run as before.

### Quality: four levels, not one

Valid JSON does not make a plan ready. `PlanQuality.Assess` distinguishes:

| Level | Examples |
|---|---|
| Structurally invalid | bad ids, unknown dependencies, cycles (as before; re-asked once) |
| Incomplete | a unit with no steps or acceptance; a requirement no unit covers; `modify` of a file that does not exist and no earlier unit creates; a fact or reference citing a missing file; a short contract when a blueprint was asked for |
| Architecturally inconsistent | a unit citing an undefined ADR or requirement; a file both modified and avoided, or created twice; a plan targeting `.tokonomy/**` or a protected path |
| Ready | none of the above blocking |

Oversized units (too many steps or files) are reported as advice, not blockers.

### Critique and revision

After the deterministic check, a **separate architect call** (`PlanCritic`) reviews the plan as a skeptical principal
engineer: coverage, explicit decisions, dependencies, unit size, actionable steps, grounding, measurable acceptance,
edge cases, contradictions, needless changes, hidden assumptions, ambiguity. It does not rewrite the plan.

The planner then revises the plan at most `planning.max_revision_rounds` times (default 1): first for deterministic
blockers, then for the critique's high/medium findings. A revision that breaks the structure is discarded and the
previous plan kept. If no round is left, the critique's findings are merged into the assessment so you see them.
Extra rounds stop once planning has cost half of `budget.max_usd_per_task`.

The plan summary shows what was discovered, the architecture, the decisions, each unit's risk and dependencies, the
readiness verdict and its issues, and the planning cost split by purpose (planning, inspection, critique, revision).
`build` asks "Build it anyway?" (default no) when the plan is not ready; `--yes` proceeds with a warning.

## 2. The plan during execution

The run executes a `PlanManager`, not a fixed list:

- **Original plan** — never modified (`plan.json` and `plan-state.json` → `original`).
- **Current plan** — the latest approved revision; revision 1 is the original.
- **Execution history** — the ledger (attempts, gates, interventions, reviews) and the transcript.
- **Lessons** — reusable knowledge from mistakes, with a scope.

Every change goes through `PlanManager.Apply`, which validates the *whole* update on a copy and applies it atomically
or not at all. A revision records its number, time, source (supervision, review, decomposition, user), reason,
evidence, trigger, the architect's decision, and every changed field with before and after values. Rules:

- completed units are immutable; `Reopen` is the only way back, is never done on a model's say-so, and is marked as
  requiring human approval;
- an update made for an older revision is rejected as stale (optimistic concurrency);
- no update may target `.tokonomy/**`, a protected path or the acceptance-test directory;
- units added during a run cannot carry acceptance tests, and a unit with tests cannot be removed;
- the revised plan must still be a valid DAG; at most `supervision.max_plan_revisions` revisions per run.

Operations: `update_unit` (spec, objective, steps, constraints, edge cases, acceptance, verification, files, avoid,
references, interfaces, decisions, corrections, dependencies, risk), `add_unit`, `remove_unit` (with a reason;
dependents are re-pointed), `split_unit`, `add_dependency`, `remove_dependency`, `add_dont`, `add_rule`, `add_lesson`.

**Lessons** have a scope: `global` (every remaining unit) or `units` (named units; parts of a split unit inherit their
parent's). A lesson may carry a `forbidden_pattern`: a regular expression (validated, length-capped, evaluated with a
timeout) that no line added by a later unit may match. The built-in **`lessons` gate** checks it on the diff, so a
corrected mistake made again fails deterministically, and the report counts it as a *repeated mistake*.

The state is saved atomically to `.tokonomy/runs/<run-id>/plan-state.json` after every change.

## 3. The worker's side

The brief (`BriefWriter.Compile`) is compiled from the current revision for every attempt: task, architecture,
binding decisions, the unit's objective, files, avoid list, references, numbered steps, interfaces, constraints, edge
cases, acceptance and verification, acceptance tests, rules and don'ts, applicable lessons, the architect's
corrections, completed units, handoffs, the revision number, and the previous failure on a retry.

It states the worker's autonomy: it may choose local names, follow local conventions, read any file and make small
implementation choices; it must not redesign, change contracts, add dependencies, touch files outside the unit,
change requirements or silently switch approach.

In strategy 5 the worker is asked to write **`tokonomy-report.json`** at the repository root:

```json
{"status": "blocked", "reason": "missing_interface", "description": "The expected interface does not exist.",
 "evidence": ["src/Services/IProductService.cs"], "attempted_actions": ["Inspected service interfaces"], "decision_required": true}
```

Statuses: `completed`, `blocked`, `clarification_required`, `unexpected_discovery`, `failed`. Tokonomy reads it only
after the link checks (never through a link), caps its size, removes it before the diff is taken (it is never
committed), and checks that the evidence paths exist. A report is a signal, never proof: a "completed" report does not
accept anything; the diff and the gates do.

## 4. Supervision

After each attempt the deterministic supervisor decides as before (accept, retry, takeover, split, fail). Then
`InterventionPolicy` decides whether the architect is called:

| Trigger | When | Levels allowed |
|---|---|---|
| `worker_blocked`, `clarification_required` | the report asks for a decision | 0, 1, 2, 3 |
| `scope_deviation` | the diff touches a path the unit must avoid | 0, 1, 2 |
| `repeated_failure` | the same gate failure twice in a row (timings and line numbers ignored) | 0, 1, 2 |
| `retry_limit`, `no_changes`, `timeout`, `worker_fault` | the classic takeover conditions | 0, 1, 2, 3 |
| `review_rejected` | the review keeps requesting changes | 0, 1, 2, 3 |

Never: for a wrong model, a workspace violation, a protected path or a new dependency (those fail or need a human,
exactly as before), or for infrastructure errors. L1 is allowed once per unit; L3 only while
`escalation.max_takeovers_per_unit` allows; interventions are capped per unit and per run.

The architect receives the current plan and revision, architecture, decisions, lessons, the unit, the trigger, the
worker's report, deviations, earlier interventions, the diff, the failure output, the relevant files, locked regions
and the remaining units. It is told not to assume its plan is right. It answers with a JSON decision (diagnosis, root
cause `worker_mistake | plan_error | missing_context | environment | unclear`, level, instructions, plan updates,
lessons, or a `human_decision`) and, for L2/L3, search/replace edit blocks.

`SupervisorPrompt.Parse` rejects a level policy does not allow, an L1 without instructions and an L2/L3 without edits.
Plan updates and lessons (plus the instructions, as a `corrections` entry on the unit) are applied as two separate
atomic revisions, so a rejected plan change never loses the lessons. Then:

| Level | What happens |
|---|---|
| 0 no action | The deterministic decision stands. |
| 1 instruction correction | The worker resumes with the diagnosis and corrected instructions (the brief now shows the new revision); optionally its attempt is discarded. A new retry window opens. |
| 2 guided correction | The edits are applied through the same guarded, all-or-nothing applier as a takeover (no protected paths, no acceptance tests, no new unapproved dependency) and every gate re-runs. Pass: committed as `[architect correction]`. Fail: the fully applied correction stays and the worker continues from it. |
| 3 takeover | As L2, but a pass is committed as `[architect takeover]` and the edit becomes a locked region. A failure resets the unit to its verified start and, if `supervision.resume_after_failed_takeover` and the caps allow, the worker resumes with the findings. |
| human decision | The unit stops with the architect's question; the worktree is reset. |

If the architect cannot be reached or its reply is unusable, the deterministic path continues, with the classic
takeover as the floor, so strategy 5 never rescues less than strategy 4. The same recovery applies to a classic
takeover that fails in strategy 5.

**Oversized units** (`Split`) are decomposed: the architect splits the unit into 2-5 parts, the split is applied as a
validated revision (dependencies re-pointed, the original's tests and acceptance moved to the last part), the worktree
is reset to the unit start and the parts run next. A part is never split again; `supervision.max_splits_per_run` caps it.

## 5. Architectural review

When a unit's gates all pass, `ReviewPolicy` decides whether the architect reviews its diff (`review.mode`):
`off`, `always`, or `risk` (default): high-risk units, units the architect intervened on, plan deviations (unplanned or
avoided files), reported discoveries, dependency changes, and medium-risk units with a large diff or several attempts.

The review looks only for what gates miss: architectural compliance, responsibilities and abstraction boundaries,
existing patterns, needless dependencies, duplicated logic, missing edge cases, API changes, performance, unrelated
changes and plan deviations. Verdicts: `approve`, `approve_with_warnings` (accepted, findings recorded),
`request_changes` (back to the worker with the findings, up to `review.max_change_requests_per_unit`, then to
supervision), `blocked` (the unit fails), `human_decision_required` (stops for a human). A review never runs on a unit
whose gates failed, so approval can never override a gate. An unavailable review accepts on the gates with a warning
unless `review.on_unavailable: fail`.

### Changes the architect wrote itself

A unit the architect completed (L2, L3, or the classic takeover in strategy 5) has passed every gate like any other
unit. It is reviewed again only when that is worth a call (`review.architect_changes: risk`, the default): on a
high-risk unit, when the edit goes outside the unit's plan, or when it is large (at least twice
`review.medium_risk_diff_lines`). That review is a separate call with a fresh context, told it did not write the change,
and can use a different model (`review.architect_review_model`), so it is not the same conversation approving its own
work. If it asks for changes, the worker continues from the architect's fix with the findings; if it blocks or needs a
human, the unit stops and nothing is committed. Small fixes inside the plan are not reviewed again: the gates already
verified them, and a second opinion from the same model adds cost more than assurance.

## 6. Records, costs and reports

Ledger schema v3 (migrated automatically and forward-only from v2; existing rows are kept):

- `llm_calls.purpose`: `planning`, `inspection`, `critique`, `revision`, `supervision`, `takeover`, `review`,
  `decomposition`, `final_review` (rows from older builds show as `unspecified`);
- `interventions`: trigger, level, root cause, diagnosis, action, outcome, resulting plan revision, evidence, cost;
- `plan_revisions`: revision, source, reason, trigger, changes (full before/after values are in `plan-state.json`);
- `unit_reviews`: verdict, why it was reviewed, findings.

`tokonomy report <run-id>` shows architect spend by purpose, unit statistics (accepted, without takeover, retries,
takeovers, interventions, plan revisions, review change requests, repeated mistakes), the interventions, the
revisions and the reviews. During a run the console shows supervision, interventions (what was found, what was done,
the new revision), plan updates and review verdicts. All architect calls count against `budget.max_usd_per_task`; none
is made once the budget is exhausted.

## Security invariants (unchanged)

The architect is never an agent with filesystem access: it sees what Tokonomy sends and answers with text. Its edits
use the guarded applier (no links, no paths outside the worktree, no `.git` / `.tokonomy`) and in strategy 5 also may
not touch protected paths or acceptance tests, nor add an unapproved dependency. Plan updates cannot target protected
paths. Model-written verification commands are shown to the worker only and never executed by Tokonomy. Acceptance
tests are restored before every gate run. Gate output and worker reports are redacted before they reach a model or a
file.

## Known limitations

- **Crash recovery** is `tokonomy run --resume <run-id>`; see [recovery.md](recovery.md).
- **Benchmark evidence is small.** See [benchmarking.md](benchmarking.md) for the live strategy 4 vs 5 comparison and
  its limits; strategy 5 stays opt-in until the evidence justifies more.
- **Reviews and supervision spend strong-model tokens.** The defaults are conservative (risk-based review, two
  interventions per unit, six per run); tune them in `policy.yaml`.
- **Lessons are enforced only when they have a pattern.** Other lessons are instructions in the brief.
- The independent review of architect-written changes uses the same provider connection; a truly independent opinion
  needs `review.architect_review_model` set to a different model.
