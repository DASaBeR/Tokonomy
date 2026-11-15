# Benchmark bench-20261115-175316-6bb4b1

1 task(s) × 1 strategy × up to 3 repeat(s). Success = hidden acceptance check passed. Claims apply only to these tasks.

| Strategy | Trials | Passed | Pass rate | Total cost | Cost per success | Cost per trial (min–max) | Tokens (fresh in + out) | Tokens per success | Cache reads | Avg time per trial |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 5 | 3 | 3 | 100% | $5.5900 (est.) | $1.8633 (est.) | $1.3957–$2.3064 | 2.03M | 677k | 7.20M | 18m 55s |

## Supervision

Totals over all trials of a strategy. Interventions are architect supervision calls (strategy 5); takeovers are architect-written units; repeated mistakes are failures of the lessons check (a corrected mistake made again); violations are high-severity review findings.

| Strategy | Units accepted | Without takeover | Worker retries | Takeovers | Interventions (L1 / L2) | Plan revisions | Review change requests | Violations | Repeated mistakes | Human decisions |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 5 | 9/9 | 100% | 1 | 0 | 0 (0 / 0) | 1 | 0 | 0 | 0 | 0 |

## Quality and behaviour

Judge = an independent architect review of each trial's final diff against the task alone (same prompt for every strategy; not charged to trials). Unplanned = files changed that no unit of the final plan named (tests excluded).

| Strategy | Judge approved | Unplanned changes | Blocker reports | L3 takeovers | Failed takeovers | Splits | Lessons | Infra failures |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 5 | 1/3 | 0 | 0 | 0 | 0 | 0 | 1 | 0 |

## Judges (reported separately)

Approved / judged per judge; "Unavailable" verdicts are not counted as judged. Unanimous = every available judge approved. Judges that share a model family with the architect or the worker can favour its output; read the judges that do not.

| Strategy | deepseek | gemini | opus | Unanimous approve |
|---|---:|---:|---:|---:|
| 5 | 2/2 | 3/3 | 1/3 | 1/3 |

## Uncertainty (directional only)

95% percentile bootstrap intervals (2000 resamples, fixed seed) over trials, treating trials as independent; this ignores that trials of one task are correlated, so the real uncertainty is wider. With this few trials per strategy, read differences as hints, not results.

| Strategy | Trials | Pass rate (95% CI) | Cost per success (95% CI) |
|---|---:|---:|---:|
| 5 | 3 | 100% (100–100%) | $1.8633 ($1.3957–$2.3064) |

## Cost by purpose (total over trials)

Shared plans are recorded once, in the trial that made them; the per-trial cost above charges them to every trial that used them.

| Strategy | architect:critique | architect:inspection | architect:planning | architect:review | architect:revision | worker:work |
|---|---:|---:|---:|---:|---:|---:|
| 5 | $0.3962 | $0.0213 | $0.4360 | $0.1191 | $0.6630 | $3.9544 |

## Trials

| Task | Strategy | # | Accepted | Run | Cost | Time | Attempts | Retries | Interventions | Revisions | Judge | Unplanned | Note |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|---:|---|
| csv-import | 5 | 2 | yes | Pass | $1.3957 | 13m 25s | 2 | 0 | 0 | 0 | Concerns | 0 |  |
| csv-import | 5 | 3 | yes | Pass | $2.3064 | 23m 45s | 5 | 1 | 0 | 1 | Approve | 0 |  |
| csv-import | 5 | 1 | yes | Pass | $1.8879 | 19m 36s | 3 | 0 | 0 | 0 | Concerns | 0 |  |

## Tokens by model

Input is fresh (uncached) prompt tokens; cache reads are listed separately and billed at a lower rate. Antigravity counts its thinking tokens in the output.

| Strategy | Model | Calls | Input (fresh) | Cache reads | Output | Cost |
|---|---|---:|---:|---:|---:|---:|
| 5 | gemini-3.8-flash-high | 10 | 908k | 7.16M | 634k | $3.9544 |
| 5 | claude-sonnet-5-5 | 14 | 402k | 45k | 88k | $1.6355 |

## Per task (passed / trials)

| Task | 5 |
|---|---:|
| csv-import | 3/3 |

Costs marked (est.) include calls that were not metered or models without a configured price. Trials on a subscription login (Claude Code, Antigravity) use plan quota, not cash: their figure is the CLI's API-equivalent estimate.
