# Strategy 4 vs strategy 5: live benchmark, 2026-11-13

**Decision: keep strategy 5 opt-in.** On these tasks it was never needed: the strong model removed every planted
mistake while planning, so no worker was blocked and no intervention happened. What strategy 5 added was overhead:
+16% cost per task and +37% execution time and execution cost, with no measurable quality gain. The benchmark therefore
cannot show whether supervision pays off when it *is* needed. It does show the price of having it switched on when it
is not.

## Setup

| | |
|---|---|
| Tasks | the 4 tasks of `examples/tasks-adaptive.yaml` (shop-demo fixture, Python). Each is a verified task with one deliberately false assumption added to its prompt (a helper, module or file that does not exist). `examples/verify_tasks.py` confirmed beforehand that the hidden tests fail on the base and pass with the reference. |
| Strong model | `claude-opus-5-5` through Claude Code 2.1.316 (subscription, `architect_provider.kind: claude-cli`) |
| Cheap model | `gemini-3.8-flash-high` through Antigravity 1.5.7 (subscription) |
| Gates | the fixture's unittest suite plus the locked-region check, in Windows Sandbox (no network) |
| Success | hidden acceptance tests only, also run in Windows Sandbox |
| Repeats | 1 trial per task and arm (12 trials) |
| Budget | `max_usd_per_task: 8.00` per trial, `--max-usd 40` in total. Every figure is an API-equivalent **estimate**: both logins are subscriptions, so nothing was billed per token. |
| Temperature | provider default; it cannot be set through either CLI |
| Code | commit `0e59d14` plus the uncommitted strategy 5 work (diff hash `d7063bd7818b` in the manifest), run from a frozen build |
| Proxy | the per-user Tokonomy proxy (`127.0.0.1:10808`) for both CLIs |

Three arms:

1. **Strategy 4** and **strategy 5**, paired (`--share-plans`): for each task, one plan made by the current planner
   (blueprint, critique, revision, file inspection) and executed by both strategies. Any difference comes from execution
   and supervision, not from planner variance. The plan's cost is charged to both.
2. **Strategy 4 with the legacy planner** (`planning: {blueprint: false, critique: false, max_revision_rounds: 0,
   inspect_files: false}`), planning on its own. Compared with arm 1's strategy 4, it isolates the effect of the new
   planner. It still received the new repository context, so this is "old prompt, no critique", not the exact old build.

After each trial an independent Opus call judged the final diff against the task alone (`--judge`, same prompt for
every arm, not charged to the trial).

## Results

| Metric | Strategy 4 | Strategy 5 | Difference |
|---|---:|---:|---:|
| Successful tasks (hidden acceptance) | 4/4 | 4/4 | 0 |
| Acceptance test success | 100% | 100% | 0 |
| Architectural compliance: judge approved / judged | 2/2 | 3/3 | n/a (3 judge calls unavailable, see below) |
| Unplanned file changes | 0 | 0 | 0 |
| Average worker retries per task | 0 | 0.25 | +0.25 |
| Average architect interventions | 0 | 0 | 0 |
| Takeovers | 0 | 0 | 0 |
| Repeated mistakes (lessons check) | 0 | 0 | 0 |
| Human intervention | 0 | 0 | 0 |
| Unit reviews | (none in strategy 4) | 1, approve with warnings | +1 call |
| Average cost per task (incl. shared plan) | $0.713 | $0.828 | **+16%** |
| Cost per successful task | $0.713 | $0.828 | **+16%** |
| Average execution cost (excl. planning) | $0.314 | $0.429 | **+37%** |
| Average execution time (excl. planning) | 3.5 min | 4.8 min | **+37%** |
| Average planning time | 1.8 min | (shared) | |

Legacy planning vs current planning, both strategy 4:

| Metric | Legacy planner | Current planner | Difference |
|---|---:|---:|---:|
| Successful tasks | 4/4 | 4/4 | 0 |
| Judge approved / judged | 4/4 | 2/2 | n/a |
| Average cost per task | $0.645 | $0.713 | +11% |
| Planning cost (4 tasks) | $0.339 | $1.596 | ×4.7 |
| Worker cost (4 tasks) | $2.239 | $1.255 | −44% |
| Average planning time | 0.5 min | 1.8 min | +1.3 min |
| Average execution time | 6.2 min | 3.5 min | −44% |
| Planning + execution | 6.7 min | 5.3 min | −21% |
| Planted false assumptions noticed in the plan | 4/4 | 4/4 | 0 |

Cost by purpose, 4 tasks:

| | planning | critique | revision | review | worker |
|---|---:|---:|---:|---:|---:|
| Strategy 4 (current planner) | $0.625 | $0.487 | $0.483 | – | $1.255 |
| Strategy 5 (same plans) | (shared) | (shared) | (shared) | $0.109 | $1.608 |
| Strategy 4 (legacy planner) | $0.339 | – | – | – | $2.239 |

## Per task

| Task | Strategy 4 | Strategy 5 | Legacy-planned 4 | What happened |
|---|---|---|---|---|
| sale-rounding-misled | ✓ $0.343 | ✓ $0.430 | ✓ $0.367 | Every plan noted that `shop.money.round_line` does not exist and put the fix in `Cart.line_total`. One unit; nothing went wrong in any arm. |
| coupon-stacking-misled | ✓ $0.608 | ✓ $0.644 | ✓ $0.790 | The plans recorded that `apply_coupons` does not exist and decided (an ADR in the blueprint) to create it on top of `apply_coupon`. The judge approved all three. |
| integer-cents-misled | ✓ $0.802 | ✓ $0.813 | ✓ $0.593 | The plans stated as a fact that `shop/currency.py` does not exist. The judge was unavailable for both current-planner trials (see below). |
| ttl-cache-misled | ✓ $1.098 | ✓ $1.426 | ✓ $0.827 | The plans created the missing `shop/lru.py`. Strategy 5 needed one extra attempt: Antigravity tried a file read that its permission rules refuse, and the run was retried as a fault, which has nothing to do with supervision. The medium-risk, large unit was then reviewed (approved with a robustness note). |

Strategy 5 cost more on every task (+1% to +30%) and took longer to execute on every task.

## Why strategy 5 showed nothing

- **The traps were caught at planning time.** Both planners, current and legacy, read the repository context, saw that
  the named helper or file did not exist, and planned around it. Strategy 5's reason to exist (a worker hitting a wrong
  assumption, the architect correcting its own plan) never arose: 0 blocker reports, 0 interventions, 0 plan revisions,
  0 lessons in all four trials.
- **The cheap worker did not fail.** Gemini 3.8 Flash passed every unit on the first gate run, apart from one
  permission fault. There were no repeated failures and no takeovers in either arm.
- **Where the extra cost comes from.** With nothing to supervise, strategy 5 still pays for the worker's structured
  report (an extra file written each attempt), a longer brief, and risk-based reviews. Gemini's output tokens were 37%
  higher (214k vs 156k) and its cache reads doubled; it made 8 calls instead of 7 (the retry). The only extra architect
  call was one review ($0.11).

## Reliability observations

- No crash, hang or orchestration error in 12 trials. One worker permission fault (strategy 5, ttl-cache) was handled
  by the normal retry.
- **Judge availability: 9 of 12 judge calls succeeded.** Two failed because Opus's safety filter flagged the
  integer-cents diff in both paired trials (a false positive; Claude Code exited with an AUP error). One reply (strategy
  4, sale-rounding) had no VERDICT line. These are judge failures, not trial failures, and they are why "architectural
  compliance" has different denominators. Every judge verdict that was returned was an approval.
- Measurement fix found here: the bench counted the judge's time in the trial's duration (judge calls took up to 19 min).
  The times above exclude it, computed from the transcripts. The runner now subtracts judge time.
- Found during the failure-path audit, not in this run: an architect call hitting the provider timeout (30 min) would
  have escaped the orchestrator as a cancellation. Fixed after this benchmark (`GuardedProvider`); the frozen build used
  here did not include the fix, but no call timed out.

## Answers

1. **Is strategy 5 better than strategy 4?** Not on this evidence. Quality was identical and strategy 5 cost more.
2. **Where is it better?** Nowhere measured; its mechanisms were never triggered.
3. **Where is it worse?** Cost (+16% per task, +37% execution) and execution time (+37%), on every task.
4. **Added cost:** about $0.115 per task here (API-equivalent), mostly cheap-worker tokens, with one $0.11 review.
5. **Fewer repeated mistakes?** Untested: there were no mistakes to repeat.
6. **Fewer takeovers or human interventions?** Untested: there were none in either arm.
7. **Worth the latency and spend?** Not when planning already removes the risk. Unknown when it does not.
8. **Reliability issues blocking production use?** None seen in strategy 5 itself. The always-on overhead is a
   cost issue, not a reliability issue.
9. **Enough evidence for a default change?** No. Four tasks, one repeat, and no trial where supervision was exercised.
10. **Further experiments needed:** see below.

**Classification: keep opt-in**, with one revision item. Strategy 5's overhead when nothing goes wrong (worker reports,
longer briefs) should be reduced before it is promoted, for example by asking for a report only when the worker is
blocked, or by keeping the report out of the first attempt's brief.

## Next experiments

1. **Traps the planner cannot see.** A wrong assumption that shows up only when the code runs (a helper that exists but
   behaves differently from its docstring, a misleading test, a config read at runtime). Only these can trigger blocker
   reports and plan corrections.
2. **Strategy 5 on the legacy planner.** Pairs a weaker plan with supervision, to see whether supervision recovers what
   the critique would otherwise have caught, and at what cost.
3. **Repeated implementation failures.** `examples/tasks-extreme.yaml`, where the cheap worker is more likely to fail.
4. **At least 3 repeats** per cell, to see variance.
5. **Overhead A/B.** Strategy 5 with `supervision.worker_reports: false` vs default, to price the report itself.
6. **A second judge model**, to avoid single-model safety false positives and self-preference.

## Raw data

All in this folder, with local user paths replaced by `%USERPROFILE%`:

- `paired-s4-s5/` and `legacy-planning-s4/`: the benchmark report (`bench-*.md`), every trial with its statistics
  (`bench-*.json`), the manifest (models, CLI versions, policy, prompt hashes, task prompt hashes, code state), a copy
  of the ledger (every model call, attempt, gate result, review and judge verdict), and per trial the plan, plan state,
  run checkpoint, transcript, worker briefs, diffs, worker reports and architect replies.
- Not kept: agent stdout/stderr and gate logs (about 47 MB), which stay in the local `runs/` folders.

Reproduce with:

```sh
tokonomy bench examples/tasks-adaptive.yaml --config examples/.tokonomy-bench-adaptive --strategies 4,5 --repeat 1 --max-usd 40 --share-plans --judge
tokonomy bench examples/tasks-adaptive.yaml --config examples/.tokonomy-bench-legacy-plan --strategies 4 --repeat 1 --max-usd 20 --judge
```
