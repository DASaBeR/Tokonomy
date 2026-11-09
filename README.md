# Tokonomy: Cut AI Coding Costs with Multi-Model Agent Orchestration

> **Make AI coding cheaper. Keep engineering quality high.**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Status: pre-alpha](https://img.shields.io/badge/status-pre--alpha-orange)

**Tokonomy (token + economy) is an open-source CLI that reduces the cost of AI-assisted software development.** It sits on top of the coding agents you already use (OpenCode today, more planned). A strong LLM plans the work once. A cheap LLM does the editing. Deterministic quality gates (build, lint, tests) decide whether the result is accepted. Tokonomy records what every successful task actually cost.

If you use Claude Code, Codex, Gemini CLI, OpenCode or Aider and your token bill keeps growing, Tokonomy is an experiment in spending expensive-model tokens only where they matter.

> **Status: pre-alpha (M2).** Planning, supervised runs with gates and retries, architect takeover with handoff and locked regions, the metering proxy, the cost ledger and the benchmark harness work. The OpenCode adapter is verified against OpenCode 1.19.5 and a real OpenAI-compatible provider (AvalAI). Not yet available: a second executor adapter (M3) and splitting oversized units. Early benchmark numbers are in [docs/benchmarking.md](docs/benchmarking.md); they are small and **not conclusive**.

## Table of contents

- [Why Tokonomy](#why-tokonomy)
- [How it works](#how-it-works)
- [Key features](#key-features)
- [Install](#install)
- [Quick start](#quick-start)
- [Benchmarking: cost per successful task](#benchmarking-cost-per-successful-task)
- [Security](#security)
- [FAQ](#faq)
- [Build from source](#build-from-source)
- [Docs](#docs)
- [Contributing](#contributing)
- [License](#license)

## Why Tokonomy

Developers routinely spend frontier-model tokens on routine edits. Cheap models handle much of that work well, but they fail on hard sections and drift from your architecture standards. A cheap run that fails is not a saving, so the right metric is **cost per successful task**, not cost per token.

No neutral, open tool currently combines these in one workflow:

- multiple coding agents and LLM providers,
- deterministic quality gates that outrank model opinion,
- an honest, per-task cost record.

Tokonomy is built to do that, and to test whether it really saves money.

### The hypothesis

A strong model used sparingly (planning, takeover, final review), plus a cheap model for execution, verified by deterministic gates, completes tasks at comparable quality for significantly lower cost per successful task than a strong model alone.

This is a hypothesis, not a claim. The project's first job is to test it with a public benchmark. If it fails, Tokonomy becomes a gates + cost ledger + multi-agent runner.

## How it works

```
Task
  ↓
PLAN      Architect (strong model) writes a contract: units, rules, don'ts
  ↓
EXECUTE   Worker (cheap model) implements each unit in an isolated git worktree
  ↓
GATE      Build, lint, tests, locked-region check: deterministic pass/fail
  ↓
DECIDE    Supervisor applies policy
  ├── Accept   → one commit per unit
  ├── Retry    → worker reruns with the gate output as feedback
  └── Takeover → architect applies structured edits, hands back to a fresh worker
  ↓
LEDGER    Tokens, cost per tier, attempts, escalations, outcome
```

Design principles:

1. **LLMs produce content; policy makes decisions.** Plans and patches come from models. Accept, retry and takeover come from deterministic rules.
2. **Gates outrank opinions.** Tests and linters beat any model's review.
3. **Judge by the diff from git, not by the agent's report.**
4. **Measure everything.** If it is not in the ledger, it did not happen.

## Key features

- **Architect / worker model routing.** Use a strong model for planning and takeover and a cheap one for the bulk of the edits.
- **Deterministic quality gates.** Build, lint, type and test commands are detected for your stack and decide acceptance.
- **Supervised retries and takeover.** Gate failures, no-change results and timeouts trigger retry, then an architect takeover with structured edits and locked regions, not a free-roaming agent.
- **Cost ledger and metering proxy.** Tokens and spend are measured per tier and per run, independent of what the agent reports.
- **Benchmark harness.** Compare five strategies (strong model alone, cheap model alone, and supervised combinations) on your own tasks, with a `--max-usd` spending cap.
- **Works with your agent.** Executors are adapters around coding-agent CLIs. Any OpenAI-compatible provider can serve the models.
- **Git-native.** Each run lives on its own branch with one commit per unit, so review and merge are normal git.
- **Single binary.** Cross-platform, one-command install, no hosted service, no telemetry by default.

## Install

```sh
# Linux / macOS
curl -fsSL https://github.com/DASaBeR/Tokonomy/releases/latest/download/install.sh | sh
```

```powershell
# Windows
irm https://github.com/DASaBeR/Tokonomy/releases/latest/download/install.ps1 | iex
```

Both installers verify SHA-256 checksums. Single-file binaries are published for Linux (x64/arm64, glibc and musl), macOS (x64/arm64) and Windows (x64/arm64).

## Quick start

```sh
cd your-repo
tokonomy init                                 # detect stack, write .tokonomy/ (set models and prices next)
tokonomy doctor                               # check git, executor, API key, gate tools, prices
tokonomy plan "Add rate limiting to /login"   # architect writes a contract (units, rules, don'ts)
tokonomy run                                  # cheap worker executes each unit; gates decide; one commit per unit
git merge tokonomy/<run-id>                   # the result lives on its own branch
tokonomy report <run-id>                      # cost per tier, tokens, architect share
```

`plan --strategy` picks how the task is done: `4` (default: architect + cheap worker + gates), `3`, `2`, `1b` or `1`. See [docs/benchmarking.md](docs/benchmarking.md).

| Command | Status |
|---|---|
| `init` | ✅ |
| `doctor` | ✅ basic checks |
| `plan "<task>"` | ✅ |
| `run` | ✅ takeover on gate failures, no changes and timeouts; oversized units and protected-path changes still fail for a human |
| `report` | ✅ |
| `bench <taskset>` | ✅ all five strategies, `--max-usd` spending cap |

## Benchmarking: cost per successful task

```sh
tokonomy bench examples/tasks.yaml --strategies 1,1b,2,3,4 --repeat 3
```

The harness runs the same tasks under each strategy, verifies results independently, and reports cost per *successful* task. Results so far are small and inconclusive; see [docs/benchmarking.md](docs/benchmarking.md) for the data and its limits. Contributions of tasksets and results are welcome.

## Security

Git worktrees isolate changes, **not execution**. Workers run with your privileges. Read [docs/threat-model.md](docs/threat-model.md) before running Tokonomy on code you do not trust.

## FAQ

**How do I reduce my AI coding agent costs?**
Stop using a frontier model for every edit. Tokonomy lets a strong model plan once and a cheap model execute, with tests and linters deciding what is accepted. Whether this saves money on your codebase is exactly what `tokonomy bench` measures.

**Is Tokonomy a new coding agent or IDE?**
No. It orchestrates existing coding-agent CLIs and does not replace them.

**Which agents and models are supported?**
The OpenCode adapter is verified today. More adapters (such as Claude Code, Codex and Gemini CLI) are planned. Models come from any OpenAI-compatible provider, including OpenRouter, DeepSeek, Gemini, Anthropic-compatible gateways, OpenAI and local servers.

**Does it send my code or telemetry anywhere?**
There is no hosted service and no telemetry by default. Your code goes only to the providers you configure.

**Is it production ready?**
No. It is pre-alpha and the core hypothesis is still being tested.

## Build from source

```sh
dotnet build
dotnet test
dotnet run --project src/Tokonomy.Cli -- --help
```

Requires the .NET 10 SDK.

## Docs

- [Configuration](docs/config.md)
- [Benchmarking](docs/benchmarking.md)
- [Threat model](docs/threat-model.md)
- [Project brief](PROJECT_BRIEF.md)

## Contributing

Issues, benchmark results and pull requests are welcome. Please run `dotnet test` before opening a PR.

## License

[MIT](LICENSE)

<sub>Keywords: AI coding agents, LLM orchestration, reduce LLM costs, token cost optimization, multi-agent coding, cost per successful task, OpenCode, Claude Code, Codex, Gemini CLI, quality gates, .NET CLI, open source.</sub>
