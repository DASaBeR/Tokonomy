# Baseline pilot: direct Claude vs strategies 4 and 5 (design, written before any trial)

**Question.** Does planning with a strong model and coding with a cheap one (strategies 4 and 5) accept about as many
tasks as the strong model working alone, at a lower cost per accepted task? This is the kill criterion of the
evaluation: if neither strategy beats direct use, Tokonomy's cost-routing story does not hold and the product should
lean on its verification and contract layer instead.

Everything here was fixed before the first trial. The pre-registration (hypothesis, success criteria, exclusion rules)
is in [`examples/tasks-pilot.yaml`](../../../examples/tasks-pilot.yaml) and is copied, with a hash, into every bench
manifest.

## Fixture

[`examples/roombook`](../../../examples/roombook): meeting-room bookings for offices in four UTC offsets (0, +01:00,
-05:00, +03:30). Python 3.11, standard library only, SQLite storage with migrations, a service layer, notifications,
a report and a CLI; about 1,050 lines with 41 visible tests. Original code, written for this pilot (no contamination).

## Tasks

| Task | Kind | What makes it hard | Hidden tests check |
|---|---|---|---|
| `back-to-back` | small bug fix (trial-first candidate) | The fix belongs in a shared helper; patching only the conflict check leaves free slots wrong | back-to-back allowed both ways and in a +03:30 room, true overlaps still refused, free slots that touch a booking |
| `csv-import` | multi-component feature (module, service rules, transaction, CLI) | **Runtime-only trap**: the Excel export starts with a UTF-8 byte order mark and contains non-ASCII text, which only shows when the file is read. Plus all-or-nothing semantics with notifications | BOM + CRLF + UTF-8 file imports, local times converted, line numbers, intra-file conflicts, rollback leaves no bookings and no notifications, partial mode, CLI output and exit codes |
| `daily-quota` | business rule with a hidden requirement | `for_user` returns cancelled bookings too (the ticket says "bookings they currently hold"); the calendar day of a UTC-05:00 room crosses UTC midnight | quota at exactly 4 h, cancelled bookings ignored, quota across rooms in the booked room's local day, both directions of the UTC-midnight trap, admins, `None`, a smaller quota |
| `maintenance` | feature with a **genuine ambiguity** | "Existing bookings that overlap a newly scheduled window need to be handled appropriately" has several defensible readings | admin-only, ordering, persistence across a restart, `MaintenanceError`, free slots. The ambiguous part is **not** tested; it is classified by a human (see pre-registration) |
| `cancel-permissions` | security fix that changes a signature | Every caller and the existing tests must change; admin cancellations must name the admin | owner, admin (with message), other member refused with no side effects, unknown actor, CLI `--as`, exit codes 1 and 2 |

[`examples/verify_pilot.py`](../../../examples/verify_pilot.py) proves, for every task: the visible tests pass on the
base; the hidden tests fail on the base; the reference solution keeps the visible tests green and passes the hidden
tests; and **10 plausible wrong solutions** (made by editing the reference: plain UTF-8, UTC instead of local time, no
rollback, counting cancelled bookings, the UTC day, forgetting free slots or admins, ...) are each caught. Last run:
all 5 tasks verified.

## Arms

| Arm | Plans | Writes code | Feedback during the run |
|---|---|---|---|
| `direct` | nobody | Claude Sonnet (`claude-code-sub`, subscription) on the raw ticket, no Tokonomy brief | none from Tokonomy; it may run `python -m unittest ...` itself (prefix allow-list, chained commands refused) |
| `4` | Claude Sonnet (`claude-cli`) | Gemini 3.8 Flash high (`agy`, subscription) | gates (visible tests, locked regions) and retries; architect takeover |
| `5` | Claude Sonnet | Gemini 3.8 Flash high | strategy 4 plus adaptive supervision and risk-based review |

Config: [`examples/.tokonomy-pilot`](../../../examples/.tokonomy-pilot). Gates run in Windows Sandbox. Each arm is used
the way a user would use it: the direct arm can run the tests because a Claude Code user would let it; the Gemini
worker cannot run commands (Antigravity's default), and gets the test results through Tokonomy's gates instead. This
asymmetry is deliberate and part of what is compared.

## Judges

Three independent reviewers read every trial's final diff against the ticket alone (never the plan, never the arm),
with the same rubric (`BenchRunner.JudgeSystem`, hash in the manifest), and are reported **separately**:

| Judge | Route | Family relation |
|---|---|---|
| `opus` | Claude Code CLI, subscription | same family as the architect and the direct arm |
| `gemini` | Antigravity CLI, subscription | same family as the worker |
| `deepseek` | AvalAI API, `deepseek-v4.1-flash`, metered ($0.15 / $0.60 per M) | neither |

DeepSeek is the tie-breaker for family bias. Each trial records `diff_chars` and `judge_diff_truncated` (judges see at
most 60,000 characters of a diff).

## Run plan

1. `python examples/verify_pilot.py` (must print "all 5 task(s) verified").
2. Smoke run, 3 trials, to check the whole chain (sandbox gates, agy worker, three judges):
   ```sh
   $env:AVALAI_API_KEY = "<key>"      # PowerShell, this session only; never in a file
   tokonomy bench examples/tasks-pilot.yaml --strategies direct,4,5 --repeat 1 --task back-to-back --seed 20261115 --max-judge-usd 0.10
   ```
3. Pilot, 45 trials, interleaved, in sittings that fit the subscriptions' limits:
   ```sh
   tokonomy bench examples/tasks-pilot.yaml --strategies direct,4,5 --repeat 3 --seed 20261115 --pause-seconds 60 --stop-after-errors 3 --max-judge-usd 0.50
   ```
   If a usage limit stops it, re-run the missing trials later with `--task` (the exclusion rules allow one re-run).
4. Classify the maintenance trials (a-d) and spot-check 7 trials; write the results next to this file.

**Expected load (estimates from the 2026-11-10 subscription pilot, not measurements).** Direct about 2-5 min per
trial, strategies 4/5 about 15-25 min, judges about 1-2 min per trial: roughly 12-14 hours of wall clock for 45
trials, so plan on two or three sittings. Real money: DeepSeek judging only, about $0.001-0.002 per call, about $0.10
for the pilot (capped at $0.50).

## Known limitations

- One repository, one language, about 1,000 lines: smaller than the evaluation's "realistic repos of a few thousand
  lines". Results are directional and say nothing about other stacks.
- 15 trials per arm. Bootstrap intervals in the report treat trials as independent; they are narrower than the truth.
- Subscription runs are not billed per token: costs are the CLIs' API-equivalent estimates. The Sonnet token count per
  accepted task is the closer proxy for what a subscriber spends.
- Strategy 5's unit review still truncates large diffs (open defect D4, fixed in Milestone 5); its effect on the
  csv-import task is possible and will be noted if seen.
- A trial is capped at $2.00 API-equivalent (`budget.max_usd_per_task`); a direct trial that hits it fails like any
  other.
- The rubric is the one earlier benchmarks used, kept unchanged for comparability. It is a binary verdict and does not
  rate the ambiguity handling; that is the human classification's job.
