# Benchmark bench-20261115-115045-a38d88

1 task(s) × 3 strategies × up to 1 repeat(s). Success = hidden acceptance check passed. Claims apply only to these tasks.

| Strategy | Trials | Passed | Pass rate | Total cost | Cost per success | Cost per trial (min–max) | Tokens (fresh in + out) | Tokens per success | Cache reads | Avg time per trial |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| direct | 1 | 0 | 0% | $0.0000 | n/a | $0.0000–$0.0000 | 0 | n/a | 0 | 27s |
| 4 | 1 | 1 | 100% | $0.3730 (est.) | $0.3730 (est.) | $0.3730–$0.3730 | 179k | 179k | 254k | 3m 17s |
| 5 | 1 | 1 | 100% | $0.8034 (est.) | $0.8034 (est.) | $0.8034–$0.8034 | 345k | 345k | 1.46M | 7m 22s |

## Supervision

Totals over all trials of a strategy. Interventions are architect supervision calls (strategy 5); takeovers are architect-written units; repeated mistakes are failures of the lessons check (a corrected mistake made again); violations are high-severity review findings.

| Strategy | Units accepted | Without takeover | Worker retries | Takeovers | Interventions (L1 / L2) | Plan revisions | Review change requests | Violations | Repeated mistakes | Human decisions |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| direct | 0/1 | n/a | 0 | 0 | 0 (0 / 0) | 0 | 0 | 0 | 0 | 0 |
| 4 | 1/1 | 100% | 0 | 0 | 0 (0 / 0) | 0 | 0 | 0 | 0 | 0 |
| 5 | 1/1 | 100% | 1 | 0 | 0 (0 / 0) | 0 | 0 | 0 | 0 | 0 |

## Quality and behaviour

Judge = an independent architect review of each trial's final diff against the task alone (same prompt for every strategy; not charged to trials). Unplanned = files changed that no unit of the final plan named (tests excluded).

| Strategy | Judge approved | Unplanned changes | Blocker reports | L3 takeovers | Failed takeovers | Splits | Lessons | Infra failures |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| direct | 0/1 | n/a | 0 | 0 | 0 | 0 | 0 | 0 |
| 4 | 1/1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| 5 | n/a | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

## Judges (reported separately)

Approved / judged per judge; "Unavailable" verdicts are not counted as judged. Unanimous = every available judge approved. Judges that share a model family with the architect or the worker can favour its output; read the judges that do not.

| Strategy | deepseek | gemini | opus | Unanimous approve |
|---|---:|---:|---:|---:|
| direct | 0/1 | 0/1 | 0/1 | 0/1 |
| 4 | 1/1 | 1/1 | 1/1 | 1/1 |
| 5 | 1/1 | 1/1 | n/a | 1/1 |

## Uncertainty (directional only)

95% percentile bootstrap intervals (2000 resamples, fixed seed) over trials, treating trials as independent; this ignores that trials of one task are correlated, so the real uncertainty is wider. With this few trials per strategy, read differences as hints, not results.

| Strategy | Trials | Pass rate (95% CI) | Cost per success (95% CI) |
|---|---:|---:|---:|
| direct | 1 | 0% (0–0%) | n/a |
| 4 | 1 | 100% (100–100%) | $0.3730 ($0.3730–$0.3730) |
| 5 | 1 | 100% (100–100%) | $0.8034 ($0.8034–$0.8034) |

## Cost by purpose (total over trials)

Shared plans are recorded once, in the trial that made them; the per-trial cost above charges them to every trial that used them.

| Strategy | architect:critique | architect:inspection | architect:planning | architect:review | architect:revision | worker:work |
|---|---:|---:|---:|---:|---:|---:|
| direct | $0.0000 | $0.0000 | $0.0000 | $0.0000 | $0.0000 | $0.0000 |
| 4 | $0.0626 | $0.0053 | $0.0301 | $0.0000 | $0.1155 | $0.1595 |
| 5 | $0.0658 | $0.0054 | $0.0871 | $0.0288 | $0.1229 | $0.4934 |

## Trials

| Task | Strategy | # | Accepted | Run | Cost | Time | Attempts | Retries | Interventions | Revisions | Judge | Unplanned | Note |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|---:|---|
| back-to-back | 4 | 1 | yes | Pass | $0.3730 | 3m 17s | 1 | 0 | 0 | 0 | Approve | 0 |  |
| back-to-back | 5 | 1 | yes | Pass | $0.8034 | 7m 22s | 2 | 1 | 0 | 0 | Unavailable | 0 |  |
| back-to-back | direct | 1 | no | Fail | $0.0000 | 27s | 1 | 0 | 0 | 0 | Concerns | - | U1: worker made no changes; attempt limit 1 reached; acceptance failed (Completed, exit 1) |

## Tokens by model

Input is fresh (uncached) prompt tokens; cache reads are listed separately and billed at a lower rate. Antigravity counts its thinking tokens in the output.

| Strategy | Model | Calls | Input (fresh) | Cache reads | Output | Cost |
|---|---|---:|---:|---:|---:|---:|
| 4 | claude-sonnet-5-5 | 4 | 85k | 26k | 8.2k | $0.2135 |
| 4 | gemini-3.8-flash-high | 1 | 65k | 227k | 21k | $0.1595 |
| 5 | gemini-3.8-flash-high | 2 | 167k | 1.44M | 57k | $0.4934 |
| 5 | claude-sonnet-5-5 | 5 | 112k | 12k | 9.5k | $0.3100 |

## Per task (passed / trials)

| Task | direct | 4 | 5 |
|---|---:|---:|---:|
| back-to-back | 0/1 | 1/1 | 1/1 |

Costs marked (est.) include calls that were not metered or models without a configured price. Trials on a subscription login (Claude Code, Antigravity) use plan quota, not cash: their figure is the CLI's API-equivalent estimate.
