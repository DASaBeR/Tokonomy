# Benchmark bench-20261115-152724-540191

1 task(s) × 2 strategies × up to 3 repeat(s). Success = hidden acceptance check passed. Claims apply only to these tasks.

| Strategy | Trials | Passed | Pass rate | Total cost | Cost per success | Cost per trial (min–max) | Tokens (fresh in + out) | Tokens per success | Cache reads | Avg time per trial |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| direct | 3 | 3 | 100% | $0.7409 (est.) | $0.2470 (est.) | $0.2394–$0.2550 | 118k | 39k | 894k | 1m 23s |
| 4 | 3 | 0 | 0% | $5.3420 (est.) | n/a | $1.1801–$2.1243 | 1.87M | n/a | 5.34M | 17m 57s |

## Supervision

Totals over all trials of a strategy. Interventions are architect supervision calls (strategy 5); takeovers are architect-written units; repeated mistakes are failures of the lessons check (a corrected mistake made again); violations are high-severity review findings.

| Strategy | Units accepted | Without takeover | Worker retries | Takeovers | Interventions (L1 / L2) | Plan revisions | Review change requests | Violations | Repeated mistakes | Human decisions |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| direct | 3/3 | 100% | 0 | 0 | 0 (0 / 0) | 0 | 0 | 0 | 0 | 0 |
| 4 | 5/8 | 100% | 0 | 0 | 0 (0 / 0) | 0 | 0 | 0 | 0 | 0 |

## Quality and behaviour

Judge = an independent architect review of each trial's final diff against the task alone (same prompt for every strategy; not charged to trials). Unplanned = files changed that no unit of the final plan named (tests excluded).

| Strategy | Judge approved | Unplanned changes | Blocker reports | L3 takeovers | Failed takeovers | Splits | Lessons | Infra failures |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| direct | 0/3 | n/a | 0 | 0 | 0 | 0 | 0 | 0 |
| 4 | 0/3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

## Judges (reported separately)

Approved / judged per judge; "Unavailable" verdicts are not counted as judged. Unanimous = every available judge approved. Judges that share a model family with the architect or the worker can favour its output; read the judges that do not.

| Strategy | deepseek | gemini | opus | Unanimous approve |
|---|---:|---:|---:|---:|
| direct | 1/2 | 3/3 | 0/3 | 0/3 |
| 4 | 0/2 | 0/3 | 0/3 | 0/3 |

## Uncertainty (directional only)

95% percentile bootstrap intervals (2000 resamples, fixed seed) over trials, treating trials as independent; this ignores that trials of one task are correlated, so the real uncertainty is wider. With this few trials per strategy, read differences as hints, not results.

| Strategy | Trials | Pass rate (95% CI) | Cost per success (95% CI) |
|---|---:|---:|---:|
| direct | 3 | 100% (100–100%) | $0.2470 ($0.2394–$0.2550) |
| 4 | 3 | 0% (0–0%) | n/a |

## Cost by purpose (total over trials)

Shared plans are recorded once, in the trial that made them; the per-trial cost above charges them to every trial that used them.

| Strategy | architect:critique | architect:inspection | architect:planning | architect:revision | worker:work |
|---|---:|---:|---:|---:|---:|
| direct | $0.0000 | $0.0000 | $0.0000 | $0.0000 | $0.7409 |
| 4 | $0.3940 | $0.0617 | $0.5215 | $0.6805 | $3.6844 |

## Trials

| Task | Strategy | # | Accepted | Run | Cost | Time | Attempts | Retries | Interventions | Revisions | Judge | Unplanned | Note |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|---:|---|
| csv-import | direct | 3 | yes | Pass | $0.2466 | 1m 22s | 1 | 0 | 0 | 0 | Concerns | - |  |
| csv-import | direct | 2 | yes | Pass | $0.2550 | 1m 18s | 1 | 0 | 0 | 0 | Concerns | - |  |
| csv-import | 4 | 2 | no | Fail | $1.1801 | 10m 41s | 1 | 0 | 0 | 0 | Concerns | 0 | U1: diff has 420 changed lines (limit 400); splitting an oversized unit is not implemented yet in this strategy (strategy 5 splits it); acceptance failed (Co... |
| csv-import | 4 | 1 | no | AbortedBudget | $2.1243 | 21m 39s | 3 | 0 | 0 | 0 | Concerns | 0 | budget of $2.00 exhausted; acceptance failed (Completed, exit 1) |
| csv-import | 4 | 3 | no | AbortedBudget | $2.0376 | 21m 30s | 2 | 0 | 0 | 0 | Concerns | 0 | budget of $2.00 exhausted; acceptance failed (Completed, exit 1) |
| csv-import | direct | 1 | yes | Pass | $0.2394 | 1m 30s | 1 | 0 | 0 | 0 | Concerns | - |  |

## Tokens by model

Input is fresh (uncached) prompt tokens; cache reads are listed separately and billed at a lower rate. Antigravity counts its thinking tokens in the output.

| Strategy | Model | Calls | Input (fresh) | Cache reads | Output | Cost |
|---|---|---:|---:|---:|---:|---:|
| direct | claude-sonnet-5-5 | 3 | 88k | 894k | 30k | $0.7409 |
| 4 | gemini-3.8-flash-high | 8 | 761k | 5.31M | 635k | $3.6844 |
| 4 | claude-sonnet-5-5 | 13 | 384k | 33k | 89k | $1.6575 |

## Per task (passed / trials)

| Task | direct | 4 |
|---|---:|---:|
| csv-import | 3/3 | 0/3 |

Costs marked (est.) include calls that were not metered or models without a configured price. Trials on a subscription login (Claude Code, Antigravity) use plan quota, not cash: their figure is the CLI's API-equivalent estimate.
