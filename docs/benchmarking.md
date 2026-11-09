# Benchmarking

Tokonomy's premise is a hypothesis. `tokonomy bench` exists to test it on your own tasks before anyone believes it.

## Primary metric

**Cost per successful task** = total spend across all trials of a strategy (attempts, retries, planning) ÷ trials that passed the hidden acceptance check.

A cheap run that fails is not a saving: its cost is still in the numerator.

## Strategies

| Id | Strategy | Question it answers |
|---|---|---|
| 1 | Strong model, single shot | Quality and cost baseline |
| 1b | Strong model + gates + retry | How much do gates and retries alone help? |
| 2 | Cheap model, single shot | How far does cheap get alone? |
| 3 | Architect plan + cheap worker, no gates | Does the split help by itself? |
| 4 | Architect plan + cheap worker + gates + supervision | Does the full approach earn its complexity? |

The decision after M3 compares **4 vs 1b** (the architect/worker split's own value) and **4 vs 1** (overall). Strategy 4 includes architect takeover (see [config.md](config.md#architect-takeover-strategy-4)); results recorded before takeover existed are not comparable with later ones.

## Writing a taskset

```yaml
config: .tokonomy            # policy, executors, prices and ledger used for every trial

tasks:
  - id: rate-limit-login
    repo: login-demo         # local dir (git repo or plain dir) or a git URL
    base: HEAD               # commit or ref to start from
    prompt: |
      ...state the public API precisely, as a real ticket would...
    acceptance:
      files: acceptance/rate-limit-login   # copied over the result AFTER the run
      cmd: ["python", "-m", "unittest", "discover", "-s", "tests", "-t", "."]
      timeout_seconds: 120
```

Rules that keep results honest:

1. **Acceptance is hidden.** Workers never see `acceptance.files`. Gates may only use the repo's visible checks.
2. **Check that each task is fair.** The hidden check must fail on the base commit and pass with a reference solution written from the prompt alone. `python examples/verify_tasks.py` does this for the `shop-demo` tasks in `examples/tasks-hard.yaml` (reference solutions live in `examples/reference/`, outside the fixture workers see). Also try near-miss implementations (off-by-one boundaries, a missing edge case) and confirm the hidden tests reject them; weak tests make every strategy look the same.
3. **Repeat trials.** Model output is non-deterministic. Use `--repeat 3` or more and look at the ranges.
4. **Scope your claims.** "On these N tasks in these repos", never in general.
5. **Watch for estimates.** Costs marked *(est.)* include unmetered calls or unpriced models.

## Running

```sh
tokonomy bench examples/tasks.yaml --strategies 1,1b,2,3,4 --repeat 3 --dry-run   # trial matrix + budget ceiling
tokonomy bench examples/tasks.yaml --strategies 1,1b,2 --repeat 3
```

**Spending limit.** `--max-usd 3.00` caps the total across the whole run. Each trial may spend at most the smaller of `budget.max_usd_per_task` and what is left, and no new trial starts once less than $0.01 remains (the run reports how many trials were skipped). Use it whenever the budget is tight, and start with the cheapest strategy (`--strategies 2`) to find out which tasks the cheap model can already solve.

Each trial runs in a fresh clone under the system temp directory. The report is printed and saved to `.tokonomy/runs/bench-<id>.md`; every trial is a normal run in the ledger (`tokonomy report <run-id>`).

Untrusted repositories execute code through their build and test commands. Run tasksets you did not write inside a VM or container.

## Results so far

> **Small and not conclusive.** One taskset, one repository fixture, one executor, one run. Do not generalise from it.

**Setup.** 8 tasks in `examples/tasks-hard.yaml` (the `shop-demo` fixture), success judged only by hidden acceptance tests. Provider: AvalAI. Strong model / architect: `claude-sonnet-5-5` ($2 in / $10 out per million tokens). Cheap worker: `deepseek-v4.1-flash` via OpenCode 1.19.5. Run on 2026-11-09 with `--repeat 3 --max-usd 4.50`; the cap stopped the run, so 4 of 48 trials did not run.

| Strategy | Trials | Passed | Total cost | Cost per success | Cost per trial (min–max) |
|---|---:|---:|---:|---:|---:|
| 1b: strong model + gates + retry | 23 | 23 (100%) | $3.51 | **$0.153** | $0.127–$0.192 |
| 4: architect plan + cheap worker + gates + supervision | 21 | 21 (100%) | $0.99 | **$0.047** | $0.024–$0.077 |

Both strategies passed every trial on 7 of the 8 tasks (3/3 each). `ttl-cache` is incomplete: 1b ran 2 of 3 trials (both passed) and strategy 4 ran none.

**What this does and does not show**

- On these tasks, strategy 4 cost about 3.3× less per successful task than the strong model alone, at the same pass rate.
- The cheap worker never failed, so no takeover was triggered. These tasks do not test where supervision earns its cost.
- With 100% pass rates on both sides, the pass-rate comparison is uninformative. Only the cost difference is measured.
- Sonnet 5.5 used about twice the tokens per task of `gpt-6.1-sol` in an earlier run ($0.153 vs $0.078 per success for 1b), so the strong baseline here is expensive; a cheaper strong model would shrink the gap.
- Not measured: strategies 1, 2 and 3 in this run, other repositories, other languages, other executors, larger tasks.

Needed before any public claim: harder tasks where the cheap worker fails, more than one repository, a second executor, and more repeats.
