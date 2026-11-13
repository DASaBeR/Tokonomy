# Project-scale run of Strategy 5: report

**Date:** 2026-11-13 · **Tokonomy:** branch `feature/adaptive-supervision-and-recovery` plus the fixes listed in section 9 · **Status of the evidence:** one run, one plan, no paired Strategy 4 result, no independent quality review yet (section 11).

> The project that was built is not named or described beyond its technical shape. It is a multi-module, full-stack web product (ASP.NET Core API, Next.js frontend, PostgreSQL, Docker deployment). The specification, plans, worker briefs, transcripts and generated code are kept privately and are not part of this repository. Everything below is about how Tokonomy behaved.

## 1. Summary

The experiment set out to compare Strategy 4 and Strategy 5 on a large, realistic build instead of isolated tasks. It ended as a **Strategy 5-only run**: Strategy 4 was dropped after it failed on an oversized unit, and the decision was to build the project with Strategy 5.

**Result: Strategy 5 completed the whole plan.** 16 of 18 units were accepted (the other two were split parents), every accepted unit passed both deterministic gates, and the final result has 89 passing backend tests and 141 passing frontend tests (12 files). About 27,300 lines were added across 167 files, of which roughly 7,900 are one generated lockfile.

| | |
|---|---|
| Models | Claude Sonnet 5.5 plans, supervises and reviews (Claude Code subscription). Gemini 3.8 Flash (high) writes the code (Antigravity subscription). Both through a proxy. |
| Passing run | $29.90 API-equivalent (worker $26.07, architect $3.83), about 5 h of active time |
| Whole experiment | $52.30 API-equivalent over 13 runs the same day, about 9 h 45 min wall time |
| Worker attempts | 22 for 16 accepted units (4 retries), **0 architect takeovers** |
| Strategy 5 activity | 3 interventions (2 splits, 1 blocker), 5 plan revisions, 3 lessons, 2 review change requests |
| Human involvement | settings decisions, and two documented edits to resume after a Tokonomy bug (section 8) |

**What the run showed about Strategy 5:**

1. **Splitting works, and is necessary at this scale.** Three units came out far above the size limit (1,377 and 10,988 lines in single passes in this run, more in earlier runs). Sonnet's splits were accepted each time, and the parts passed on their first attempt.
2. **Architectural review found two real defects that every gate had passed**, a global monkey-patch of the API client and fetch (high severity), and broken backup commands plus documentation that contradicted the design (high and medium). Both were fixed on the next attempt. Strategy 4 has no review step.
3. **Reviews are limited by diff truncation.** On large units the reviewer saw only part of the diff, and said so in at least five reviews of the passing run (U6a, U6b, U6d, U7, U8). The files it could not read include the very file a lesson was about.
4. **The strong model diagnosed two environment blockers correctly and refused to work around a permission control.** Both were Tokonomy or environment problems, not model failures.
5. **Lessons were created (3) but never shown to prevent a repeat.** One was honoured by a corrected attempt; no unit repeated a lesson's mistake, so there is no evidence either way.
6. **The cheap worker was never taken over.** 18 of its 22 attempts passed the gates. Of the other four, two were over the size limit (and were split) and two were infrastructure faults. Two of the 18 passing attempts were then rejected by review and redone, which is where the run's four retries come from (two faults, two review requests).

**What this does not show:** whether Strategy 5 is better than Strategy 4. Strategy 4's only attempt used different settings (default limits, no worker shell), so the two outcomes are confounded (section 10).

## 2. Setup

| | |
|---|---|
| Strategy | 5 (Strategy 4's loop plus adaptive supervision, splitting, lessons, plan revisions, risk-based review) |
| Strong model | `claude-sonnet-5-5` via Claude Code (subscription), architect provider `claude-cli` |
| Worker | `gemini-3.8-flash-high` via Antigravity 1.5.8 (subscription) |
| Network | Both CLIs through a per-user proxy (`tokonomy proxy`) |
| Cost | No cap (user decision). All figures are API-equivalent estimates; nothing was billed per token. |
| Gates | Two scripts (`gates/gate.py`): backend build and tests, frontend typecheck, lint, build and tests; both pass trivially while their half does not exist. Plus the locked-regions check. |
| Gate isolation | `host-unsafe` (the Windows Sandbox on the test machine had no network, so restores could not run) |
| Plan | One plan written by Sonnet during an earlier Strategy 4 run, reused unchanged for every later run with `run --plan <id> --again` (the plan's strategy field was set to 5) |

**Deviations from Tokonomy defaults (all deliberate, all recorded in `config/`):**

| Setting | Default | Used | Why |
|---|---:|---:|---|
| `escalation.max_diff_lines` | 400 | 1000 | the units of a project this size are naturally larger; three earlier runs stopped on 400 |
| `supervision.max_splits_per_run` | 2 | 8 | same |
| Worker shell | restricted | **any command** (`permissions.allow_all_commands`) | the plan needs generated code and tool commands; Antigravity 1.5.8 cannot enforce a command list (section 9) |
| Antigravity `--sandbox` | on | **off** in allow-all mode | with it, the first shell command ends the session (exit 2) |
| `review_on_new_dependency` | true | false | a halt per package is not a realistic workflow |
| `escalation.allowed_dependencies` | empty | a list of common packages | so a strong-model correction is not halted for approval |
| Gate isolation | sandbox | host | see above |

## 3. The task

A product with an API, background jobs, a database and a web UI, built in nine planned units:

| Unit | Content (generic) |
|---|---|
| U1 | foundation: repository scaffolding, backend skeleton, test harness, CI |
| U2 | identity, first-run lock, authentication, roles, user management, audit log |
| U3 | metrics collection module and endpoints |
| U4 | third-party service integration module |
| U5 | background-jobs module |
| U6 | frontend foundation (scaffold, API client, component library, auth and shell, first-run wizard) |
| U7 | dashboard and chart pages |
| U8 | management pages (lists, team, settings, audit) |
| U9 | deployment: compose file, Dockerfiles, installation docs |

The plan had 19 advisory issues (mostly "unit too large"). A strong-model-written plan of this scale was about 250 KB, and the worker briefs 75-84 KB each.

## 4. Timeline of the experiment

| # | Run (UTC) | Outcome | API-eq. cost | Cause |
|---|---|---|---:|---|
| 1 | 08:40 two trials | failed at `git clone` | $0 | git "dubious ownership" on the new repo; fixed by a global `safe.directory` entry |
| 2 | 08:40 Strategy 4 | **invalid** | $3.02 | my gate script passed an invalid switch to `dotnet build`, so an oversized diff could not be verified |
| 3 | 09:06 Strategy 5 trial | stopped by me | $1.96 | the benchmark was invalid (run 2) |
| 4 | 09:25 Strategy 4 (the plan used later) | **failed at U2** | $4.18 | U1 accepted; U2 was 2,091 lines with one failing test, over the 400 limit; Strategy 4 cannot split |
| 5 | 10:05 shared-plan bench | stopped | $1.68 | decision to run Strategy 5 only on run 4's plan |
| 6 | 10:28 | failed at start | $0 | the config sat outside the repository, so the worktree was created in the wrong repository |
| 7 | 10:28 Strategy 5 (limit 400) | **failed at U2c** | $4.90 | 3 units accepted; U2 was split, a 551-line part exceeded 400, and a part cannot be split again |
| 8 | 11:40 | stopped | $0.63 | my edit of the diff limit had not applied |
| 9 | 11:49 Strategy 5 (limit 1000) | **stopped at U2b** | $5.60 | 5 units accepted; the unit needed a database-migration command and the worker had no shell. The strong model diagnosed this and asked for a human decision. |
| 10 | 12:55 | stopped | $0.17 | my literal command allowlist was not going to work |
| 11 | 13:07 | failed in 2 min | $0.28 | `--sandbox` kills Antigravity at the first shell command (exit 2) |
| 12 | 13:11 Strategy 5 | **passed** (with one resume) | $29.90 | see sections 5-8 |

The passing run itself stopped once, at U6a, for a Tokonomy bug (section 9), and was resumed after the fix.

## 5. The passing run, unit by unit

| Unit | Attempts | Diff lines | Shell cmds | Review | Notes |
|---|---:|---:|---:|---|---|
| U1 foundation | 1 | 957 | 38 | approve, warnings | |
| U2 identity and auth | 1 | 2,432 | 19 | approve, warnings | the unit that failed under Strategy 4 |
| U3 metrics collection | 1 | 1,521 | 16 | approve, warnings | |
| U4 third-party integration | 1 | 1,760 | 16 | approve, warnings | |
| U5 background jobs | 1 | 1,377 | 19 | n/a | **split** (plan error): 1,377 lines was over the limit |
| U5a data model and migration | 1 | 398 | 21 | approve, warnings | |
| U5b validation and executor | 1 | 264 | 45 | skipped (routine) | |
| U5c scheduler/runner | 1 | 174 | 22 | approve, warnings | |
| U5d endpoints | 1 | 209 | 31 | approve, warnings | |
| U6 frontend foundation | 1 | 10,988 | 55 | n/a | **split** (plan error): scaffold, lockfile, tokens, client, components and wizard in one pass |
| U6a scaffold | 2 | 8,508 | 86 | approve, warnings | attempt 1 blocked by a Tokonomy bug (section 9); attempt 2 took 38 min (installs) |
| U6b client and types | 1 | 1,527 | 19 | approve, warnings | |
| U6c component library | 1 | 1,288 | 20 | skipped (routine) | |
| U6d auth and shell | 2 | 1,329 / 1,490 | 21 / 15 | **change requested**, then approve | see section 6 |
| U6e wizard and login | 1 | 1,748 | 18 | approve, warnings | |
| U7 dashboard and charts | 1 | 1,695 | 23 | approve, warnings | |
| U8 management pages | 1 | 2,900 | 31 | approve, warnings | largest accepted unit |
| U9 deployment and docs | 3 | 704 / 718 / 718 | 32 / 15 / 10 | **change requested**, then approve | see section 6; attempt 2 lost to an infrastructure fault |

Unit durations were typically 11-15 minutes for backend and frontend feature units, 9 minutes for a corrected attempt, and 38 minutes for the framework install.

## 6. What Strategy 5's mechanisms did

### Splitting (3 events in the run, plus earlier ones)
- Triggered by the size limit each time, and diagnosed by Sonnet as a **plan error** ("the unit mixed several responsibilities"), not a worker failure.
- The parts were ordered by dependency so each stayed buildable; every part was accepted on its first attempt.
- Limits seen: a part cannot be split again, and the 400-line default stopped three earlier runs. At 1000 lines the run never failed on size.

### Architectural review (18 review events, 16 Sonnet calls, $3.33)
| Verdict | Count |
|---|---:|
| approve with warnings | 14 |
| skipped as routine | 2 |
| **request changes** | **2** |

- **U6d (high severity):** the session-expiry handling replaced every method of the API client and the global `fetch`, excluding endpoints by method name. The plan asked for a hook inside the central request function. All gates had passed. A corrected attempt (9 minutes) fixed it. A lesson was recorded.
- **U9 (high and medium):** the volume backup and restore commands could not work (they treated an image as a compose service and used an unprefixed volume name, so a backup would have silently captured an empty volume); the docs described a password policy, an audit field and a configuration fallback that contradict the design; the Dockerfiles were never built. The corrected attempt fixed most of this. The final review still found one wrong permission name in the docs, which the one-change-request limit let through.
- **Real low-severity finds** included a number formatter that returns "undefined" for fractions between 0 and 1, a form that blocks values the backend accepts, a dead link, and a chart component with about 1,200 keyboard tab stops.
- **Limit:** on large units the diff given to the reviewer was truncated. At least five reviews of the passing run state that files (a hook, an auth file, config, design tokens, lower halves of pages) went unreviewed.

### Lessons (3)
| Lesson | Source | Effect observed |
|---|---|---|
| L1: guard platform-specific build workarounds | frontend scaffold review | not triggered again |
| L2: no monkey-patching of the API client; use a hook in the central request function | U6d review | the corrected attempt complied; no later unit repeated it |
| L3: docs must match the design and runnable commands must run | U9 review | the next attempt fixed most doc issues; one wrong claim remained |

Lessons are cheap (they ride on the review), but **no case showed a lesson preventing a repeated mistake**.

### Interventions and takeovers
- Three interventions, all at level 0: two splits and one blocker. No instruction correction (L1), guided correction (L2) or takeover (L3) was needed in the passing run.
- Twice (earlier run and the U6a stop) the strong model **escalated an environment problem to a human instead of retrying or taking over**, and in the second case explicitly declined to bypass a permission control. These were correct calls.

### Worker reports
The worker's own report said "Completed" in every case, including an earlier attempt that did not build. Gates and review, not the report, decided every outcome.

## 7. Cost and time

Passing run, API-equivalent (Tokonomy's ledger):

| Role | Purpose | Calls | Input tokens | Output tokens | Cost |
|---|---|---:|---:|---:|---:|
| Worker | implementation | 22 | 6.29 M (+162 M cache reads) | 1.81 M | $26.07 |
| Architect | review | 16 | 1.45 M | 26.6 k | $3.33 |
| Architect | decomposition | 2 | 104 k | 11.9 k | $0.33 |
| Architect | supervision | 1 | 71.8 k | 1.9 k | $0.17 |
| **Total** | | **41** | | | **$29.90** |

- Architect share: 13% of cost, 17% of tokens (Tokonomy's own measure).
- Reviews were 11% of total cost.
- The whole experiment (13 runs) cost **$52.30**; the failed or stopped runs account for $22.40, mostly planning and unit work that had to be repeated.
- The plan (about 14 minutes of Sonnet time, created inside run 4) is inside that run's cost and was reused.

Time: about 2 h 16 min before the U6a stop and 2 h 43 min after the resume (about 5 h active). The experiment ran from 09:40 to 19:24 local, 9 h 44 min wall time, most of it spent on failed attempts, not on the final build.

**Real money:** none. Both models ran on subscriptions, so no per-token billing occurred. The figures show what the run would cost on a metered API.

## 8. Human intervention log

| When | What | Why |
|---|---|---|
| start | chose scope (first two phases), host gates, no price cap, Sonnet as the strong model, both CLIs through the proxy | scope and environment decisions |
| run 7 | raised the diff limit to 1000 and splits to 8 | three runs stopped on the defaults |
| run 9 | chose "allow the worker to run any shell command" | the strong model asked for a human decision (migration command) |
| before run 12 | approved a pre-approved package list | corrections were halted for approval once |
| run 12, U6a | **two edits to run records**: ledger status Fail to Running, and U6a progress failed to pending (backups kept) | `--resume` refuses a run that stopped for a human decision, even after the cause was fixed in code. The proper fix (resume after a human decision) is not built. |

No code was written or edited by hand in the generated project. All of its content came from the models.

## 9. Tokonomy problems found and fixed during the experiment

| Problem | Effect | Status |
|---|---|---|
| `IgnoredEntriesAsync` denied a whole folder when it held only ignored leftovers (`--directory` collapses it), so after a split reset the worker could write nothing in the frontend folder | stopped the passing run at U6a | **fixed**, two tests added |
| Antigravity `--sandbox` kills the session at the first shell command (exit 2) | runs 10-11; shell access impossible | worked around: allow-all mode drops the flag. Restricted mode with allowed commands is probably affected too (not tested). |
| Antigravity 1.5.8 does not enforce command allowlists: even an allowed command ends the session | per-command approval impossible on this provider | documented; the grant falls back to "unit-wide plus audit" |
| a worker that starts a long command tries to read its own task log, which the credential deny rule blocks | cost one attempt in U9 | **open**: narrow the deny to the task-log folder |
| `tokonomy report` labels a run "Strategy 4" when it was a copied plan executed as Strategy 5 | reporting label only | **open** |
| `--resume` refuses a run that stopped on a human decision | forced two record edits | **open** |
| split parts cannot be split again; reviewer sees truncated diffs; one change request per unit | stopped a run; weakened reviews | **open** |
| OpenCode's default template already allows all shell commands, so its "restricted" setting was never restricted | none in this run | **open**, noted |

**New Tokonomy features built during the experiment (tested, in this branch):**
- *Allow all shell commands*: a per-executor option (`permissions.allow_all_commands`), a setup question, and `init --worker-shell restricted|all`, mapped per CLI (Antigravity, Claude Code, Codex, OpenCode and custom CLIs). Documented in `docs/config.md`.
- *Shell grant* (stage one of per-unit shell approval): a per-unit grant of exact commands, enforced per command where the CLI can (Claude Code, OpenCode, custom CLIs with `shell_grant_args`), unit-wide with an audit for Antigravity, and by sandbox level for Codex. Not yet connected to planning or prompts: the approval flow itself is not built.

## 10. Limitations and threats to validity

- **One run, one plan, one project.** Nothing here generalises beyond "on this project, with these two models".
- **No paired comparison.** Strategy 4 failed once, at default limits and without a worker shell; Strategy 5 passed at a raised limit and with a worker that could build and test itself. The difference could come from either the strategy or the settings. This report does not claim Strategy 5 is better than Strategy 4.
- **The settings changed during the experiment** (section 2), and the final run used the best-known configuration. A fresh run on default settings would have stopped earlier.
- **Gates verify that code builds and its own tests pass.** They say little about whether behaviour matches the requirements.
- **Review quality is bounded by truncated diffs**, and one model reviewed another's output without a second opinion.
- **Self-assessment risk:** the strong model both planned and reviewed.
- **Costs are API-equivalent estimates** and depend on Tokonomy's price table and the CLIs' own token reports.
- **Host gates, all-commands shell, no sandbox:** a less safe setup than Tokonomy's default. The run executed model-written code on the host with the user's privileges.
- **The plan was written by Strategy 4's planner**, so planner variance between strategies was not measured.

## 11. What is not done

| Item | State |
|---|---|
| Hidden black-box acceptance suite | written, **never run** against the result (so "passes its gates" is not "meets the requirements") |
| Independent blind quality review (spec conformance, architecture, security) | not done |
| Image builds and a running compose stack | **never built or run** (no container engine on the test machine) |
| Strategy 4 on the same plan with equal settings | not done |
| Repeats | none |

## 12. Answers to the questions in the experiment brief

1. **Which strategy produced the better implementation?** Not determined (no paired result, no quality review).
2. **Meaningful or marginal?** Not determined.
3. **Did Strategy 5 improve architecture or completeness?** Review changes improved two units (a design flaw and the deployment docs). No before-and-after comparison with Strategy 4 exists.
4. **Did it prevent problems Strategy 4 could not handle?** It handled oversized units that stopped Strategy 4, but the two runs differed in settings (section 10). It caught two defects through review that the gates passed; Strategy 4 has no review.
5. **Did it reduce human intervention?** Not measurable. Both human stops came from Tokonomy or environment problems.
6. **Added time and cost versus Strategy 4?** Not measurable. Strategy 5's own overhead in this run: reviews 11% of cost, architect 13%, plus about 9 minutes per corrected attempt.
7. **Worth the overhead on large projects?** On this evidence, probably yes for review and splitting, which together addressed the failures seen. Unproven without a paired run.
8. **Opt-in, default, or automatic?** Keep opt-in for now. A reasonable next step is automatic selection for plans whose units exceed the size limit, and enabling review for them, after a paired comparison.
9. **Mechanisms with real value:** splitting; architectural review (when it can see the diff); the strong model's blocker diagnosis and escalation.
10. **Overhead without measured benefit:** lessons (no repeat prevented), worker reports (unreliable), reviews of truncated diffs (partial value), skipped reviews (no information).

## 13. Recommendations for Tokonomy

1. Let the reviewer see the whole diff (chunked or by file), and flag when it could not.
2. Allow a split part to be split again, and report the reason clearly when the limit stops a run.
3. Build "resume after a human decision" and the per-unit shell approval flow (stages two to five are planned).
4. Narrow the credential deny so agents can read their own task logs.
5. Re-test Antigravity's command rules and `--sandbox` on each CLI update; keep allow-all as an explicit, warned choice.
6. Make OpenCode's "restricted" mode actually restrictive.
7. Fix the "Strategy" label in `tokonomy report` for reused plans.
8. Run the missing pieces: acceptance suite, blind review, and a paired Strategy 4 run with identical settings and at least three repeats.

## 14. Raw data

`metrics.json` (this folder) holds the per-unit, per-run and cost data. The configuration is in `config/` and the gates in `gates/`. The plan, briefs, transcripts, diffs, run records and the acceptance suite contain the specification or generated code of a private project and are **not published**.
