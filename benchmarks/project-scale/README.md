# Project-scale benchmark (Strategy 5)

A single run of Tokonomy's Strategy 5 on a large, realistic build: a multi-module full-stack web product (ASP.NET Core API, Next.js frontend, PostgreSQL, Docker deployment) built from a written specification. The goal was to see how adaptive supervision behaves on a project, not on isolated tasks.

**Read [benchmark-report.md](benchmark-report.md) first.** It states what was and was not measured. In short: one run, one plan, a pass at the gates, no independent quality review, no paired Strategy 4 result.

## Contents

| File | What it is |
|---|---|
| [benchmark-report.md](benchmark-report.md) | the full report: setup, deviations from defaults, timeline of the 13 runs, per-unit results, what each mechanism did, cost and time, human intervention log, Tokonomy bugs found, limitations, recommendations |
| [metrics.json](metrics.json) | machine-readable data: per-unit attempts, diff sizes, shell commands, review verdicts, events, lessons, cost by role and purpose, all runs of the day |
| [config/](config/) | the `policy.yaml`, `executors.yaml` and `prices.yaml` used (paths replaced by `<TOKONOMY_REPO>` and `<WORKTREE_ROOT>`) |
| [gates/gate.py](gates/gate.py) | the gate script: backend build and tests, frontend typecheck, lint, build and tests. It passes while a half does not exist yet, so early units are not blocked. |

## Not published

The specification, the generated plan, worker briefs, transcripts, diffs, run records and the acceptance tests describe or contain a private project, so they are not in this repository. `metrics.json` is generated from them with unit names reduced to generic labels.

## Reproducing the setup on your own project

1. Use a project with a written specification and an empty or small repository.
2. Copy `config/` to `<repo>/.tokonomy/`, replace the placeholders, and set `models` and `architect_provider` for your own logins.
3. Make the specification part of the task text or a file in the repository, then:

```sh
tokonomy plan "<task>" --strategy 5
tokonomy run --keep-worktree
# after an interruption:
tokonomy run --resume <run-id> --keep-worktree
```

4. Read `tokonomy report <run-id>` for cost by purpose, interventions, reviews and revisions.

**Safety:** the configuration used here runs gates on the host (`host-unsafe`) and lets the worker run any shell command (`permissions.allow_all_commands`). That is deliberate for a trusted project on a throwaway machine and is **not** Tokonomy's default. Do not copy it to a project or a machine you do not trust. See [docs/config.md](../../docs/config.md) for the safer defaults.
