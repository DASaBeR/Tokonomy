# Taskrail

> **Make AI coding cheaper. Keep engineering quality high.**

Taskrail is an open-source CLI that coordinates coding agents, routes work across models, enforces deterministic quality gates, and measures the real **cost per successful task**.

> **Status: pre-alpha (M0).** The CLI skeleton, config, ledger and release pipeline work. Planning, supervised runs and benchmarking land in M1–M3. "Taskrail" is a working name.

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
taskrail init        # detect stack, write .taskrail/policy.yaml and executors.yaml
taskrail doctor      # check git, executor, API key, gate tools, prices
taskrail report      # show the ledger
```

| Command | Status |
|---|---|
| `init` | ✅ |
| `doctor` | ✅ basic checks |
| `report` | ✅ |
| `plan "<task>"` | M1 |
| `run` | M1 |
| `bench <taskset>` | M1 (strategies 1, 1b, 2), M3 (all) |

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
- [Threat model](docs/threat-model.md)
- [Project brief](PROJECT_BRIEF.md)

## License

Apache-2.0
