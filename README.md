# Taskrail

> **Make AI coding cheaper. Keep engineering quality high.**

Taskrail is an open-source CLI that coordinates coding agents, routes work across models, enforces deterministic quality gates, and measures the real **cost per successful task**.

> **Status: pre-alpha (M2).** Planning, supervised runs with gates and retries, architect takeover with handoff and locked regions, the metering proxy, the cost ledger and the benchmark harness work. The OpenCode adapter is verified against OpenCode 1.19.5 and a real provider (AvalAI, OpenAI-compatible). Not yet available: a second executor adapter (M3) and splitting oversized units. Early benchmark numbers are in [docs/benchmarking.md](docs/benchmarking.md); they are small and not conclusive. "Taskrail" is a working name.

## The hypothesis

A strong model used sparingly (planning, takeover, final review), plus a cheap model for execution, verified by deterministic gates, completes tasks at comparable quality for significantly lower cost per successful task than a strong model alone.

This is a hypothesis, not a claim. The project's first job is to test it with a public benchmark. If it fails, Taskrail becomes a gates + cost ledger + multi-agent runner.

## Principles

1. LLMs produce content; policy makes decisions.
2. Gates outrank opinions.
3. Judge by the diff from git, not by the agent's report.
4. Measure everything: if it is not in the ledger, it did not happen.

## Install

```sh
# Linux / macOS
curl -fsSL https://github.com/taskrail/taskrail/releases/latest/download/install.sh | sh
```

```powershell
# Windows
irm https://github.com/taskrail/taskrail/releases/latest/download/install.ps1 | iex
```

Both installers verify SHA-256 checksums. Single-file binaries are published for linux (x64/arm64, glibc and musl), macOS (x64/arm64) and Windows (x64/arm64).

## Quick start

```sh
cd your-repo
taskrail init                          # detect stack, write .taskrail/ (set models and prices next)
taskrail doctor                        # check git, executor, API key, gate tools, prices
taskrail plan "Add rate limiting to /login"   # architect writes a contract (units, rules, don'ts)
taskrail run                           # cheap worker executes each unit; gates decide; one commit per unit
git merge taskrail/<run-id>            # the result lives on its own branch
taskrail report <run-id>               # cost per tier, tokens, architect share
```

`plan --strategy` picks how the task is done: `4` (default, architect + cheap worker + gates), `3`, `2`, `1b` or `1`. See [docs/benchmarking.md](docs/benchmarking.md).

```sh
taskrail bench examples/tasks.yaml --strategies 1,1b,2,3,4 --repeat 3
```

| Command | Status |
|---|---|
| `init` | ✅ |
| `doctor` | ✅ basic checks |
| `plan "<task>"` | ✅ |
| `run` | ✅ takeover on gate failures, no changes and timeouts; oversized units and protected-path changes still fail for a human |
| `report` | ✅ |
| `bench <taskset>` | ✅ all five strategies, `--max-usd` spending cap |

## Security

Git worktrees isolate changes, **not execution**. Workers run with your privileges. Read [docs/threat-model.md](docs/threat-model.md) before running Taskrail on code you do not trust.

## Build from source

```sh
dotnet build
dotnet test
dotnet run --project src/Taskrail.Cli -- --help
```

Requires the .NET 10 SDK.

## Docs

- [Configuration](docs/config.md)
- [Benchmarking](docs/benchmarking.md)
- [Threat model](docs/threat-model.md)
- [Project brief](PROJECT_BRIEF.md)

## License

MIT
