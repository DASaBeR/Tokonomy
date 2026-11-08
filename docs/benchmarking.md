# Benchmarking

Taskrail's premise is a hypothesis. `taskrail bench` exists to test it on your own tasks before anyone believes it.

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

The decision after M3 compares **4 vs 1b** (the architect/worker split's own value) and **4 vs 1** (overall). Until takeover lands in M2, strategy 4 fails a unit wherever it would escalate.

## Writing a taskset

```yaml
config: .taskrail            # policy, executors, prices and ledger used for every trial

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
2. **Check that each task is fair.** The hidden check must fail on the base commit and pass with a reference solution written from the prompt alone. The seed tasks in `examples/` were verified this way.
3. **Repeat trials.** Model output is non-deterministic. Use `--repeat 3` or more and look at the ranges.
4. **Scope your claims.** "On these N tasks in these repos", never in general.
5. **Watch for estimates.** Costs marked *(est.)* include unmetered calls or unpriced models.

## Running

```sh
taskrail bench examples/tasks.yaml --strategies 1,1b,2,3,4 --repeat 3 --dry-run   # trial matrix + budget ceiling
taskrail bench examples/tasks.yaml --strategies 1,1b,2 --repeat 3
```

Each trial runs in a fresh clone under the system temp directory. The report is printed and saved to `.taskrail/runs/bench-<id>.md`; every trial is a normal run in the ledger (`taskrail report <run-id>`).

Untrusted repositories execute code through their build and test commands. Run tasksets you did not write inside a VM or container.
