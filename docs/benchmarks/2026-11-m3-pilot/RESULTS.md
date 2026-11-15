# Baseline pilot, first results (2026-11-15): direct Claude vs strategies 4 and 5 on one hard task

This is an **interim** result: one task (`csv-import`), three trials per arm, plus a three-trial smoke run on
`back-to-back`. It is not the 45-trial pilot described in [DESIGN.md](DESIGN.md). Read it as directional.

Setup (as designed): Claude Sonnet 5.5 is the architect (strategies 4 and 5) and the whole worker in the `direct` arm;
Gemini 3.8 Flash (high) writes the code in strategies 4 and 5. Success = hidden acceptance tests pass. Costs are the
CLIs' API-equivalent estimates (both models ran on subscriptions), not cash. Raw bench reports are in
[reports/](reports/).

## Result on `csv-import` (multi-part feature, hidden BOM trap)

| Arm | Passed | Cost per trial | Cost per accepted task | Sonnet tokens per accepted task (fresh in + out) | Avg time per trial |
|---|---:|---:|---:|---:|---:|
| direct (Sonnet alone) | 3 of 3 | $0.239 to $0.255 | **$0.247** | **39k** | 1m 23s |
| strategy 4 | 0 of 3 | $1.180 to $2.124 | none accepted | none accepted | 17m 57s |
| strategy 5 | 3 of 3 | $1.396 to $2.306 | **$1.863** | **about 163k** | 18m 55s |

Token use by model (all trials of an arm; input is fresh/uncached, Gemini output includes thinking tokens):

| Arm | Model | Calls | Input | Cache reads | Output | Cost |
|---|---|---:|---:|---:|---:|---:|
| direct | Sonnet 5.5 | 3 | 88k | 894k | 30k | $0.741 |
| 4 | Gemini 3.8 Flash high | 8 | 761k | 5.31M | 635k | $3.684 |
| 4 | Sonnet 5.5 | 13 | 384k | 33k | 89k | $1.658 |
| 5 | Gemini 3.8 Flash high | 10 | 908k | 7.16M | 634k | $3.954 |
| 5 | Sonnet 5.5 | 14 | 402k | 45k | 88k | $1.636 |

Judges (approved / judged), reported separately: direct: DeepSeek 1/2, Gemini 3/3, Opus 0/3; strategy 4: 0/2, 0/3, 0/3;
strategy 5: 2/2, 3/3, 1/3. The judges were not spot-checked by a human yet, and they disagree with each other.

Smoke run (`back-to-back`, 1 trial per arm): direct $0.095 to $0.115, strategy 4 $0.373, strategy 5 $0.803; all three
passed (the first direct attempt failed because of a harness bug, fixed below).

## What the data says

1. **Strategy 5 did not reduce the strong model's token use. It increased it.** The Sonnet architect's planning,
   critique, revision, review and inspection calls used about 4x the Sonnet tokens that Sonnet needed to do the
   whole job alone. Cost per accepted task was about 7.5x direct.
2. **The cheap worker was not cheap here.** Gemini 3.8 Flash at high effort produced about 635k output tokens
   (thinking included) per three trials, 20x all of direct Sonnet's output, and it was 70% of each cheap arm's cost.
3. **Strategy 5 was reliable where strategy 4 was not**: 3 of 3 against 0 of 3. Strategy 4 failed once on its
   400-line unit limit and twice on the $2.00 per-trial budget. Caveat: strategy 5 ran with the cap raised to $5.00 (one
   of its three trials cost $2.31). Strategy 4 was not re-run at $5.00, so that comparison is not like for like.
4. **Supervision did little visible work.** Strategy 5 recorded 0 supervisor interventions, 1 plan revision and 1 worker
   retry over three trials.
5. **Against the pre-registered rule** ("an arm beats direct if its cost per accepted task is lower and it passes at
   least as many trials as direct minus 2"), neither strategy 4 nor strategy 5 beats direct on this task. The kill
   criterion of the evaluation is met at pilot level, on this task.

## Limits (do not generalize beyond them)

- One repository of about 1,000 lines, one language, one task, 3 trials per arm. Bootstrap intervals are degenerate here.
- The task is solvable by one strong model in about a minute. The cheap-worker idea has not been tested on work where
  the strong model's own tokens are a real constraint (larger, multi-phase tasks).
- Strategy 4 and the direct arm ran under a $2.00 per-trial cap, strategy 5 under $5.00.
- The worker (Gemini 3.8 Flash high) was fixed; a lower-effort worker might shift the cost picture. Not tested.
- Costs are API-equivalent estimates from subscription CLIs, not billed amounts.
- Judge verdicts are unreviewed. The Opus judge failed on earlier smoke attempts (a parse failure, then a process
  crash) and answered on the final runs.
- Trials excluded as infrastructure failures: two direct trials killed by connection resets through a flaky proxy, and
  one trial from a run stopped by hand. The proxy was a local HTTP proxy that dropped long Claude connections.

## Harness fixes made during the pilot

- The `direct` arm's multi-line ticket was cut at the first line break by the Windows `claude.cmd` shim. It now goes in
  on stdin. A test covers it.
- A review verdict wrapped in markdown (`**VERDICT: APPROVE**`) is now read. A test covers it.
- The example config's per-trial budget was raised from $2.00 to $5.00 for the strategy 5 trials (noted in the file).
