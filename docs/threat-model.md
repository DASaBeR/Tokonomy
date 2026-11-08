# Taskrail threat model (v1)

Status: draft for M0. It must be reviewed before the first executor ships (M1) and whenever a trust boundary changes.

## What Taskrail does

Taskrail runs coding-agent CLIs ("workers") as subprocesses inside a git worktree of the user's repository. It calls an LLM provider directly for architect work. It runs deterministic gates (build, lint, test) and records cost and outcomes in a local SQLite ledger.

## The key fact

**Git worktrees isolate changes, not execution.** A worker is an LLM-driven program that can run arbitrary commands with the user's privileges. Taskrail v1 is **not a sandbox**. A malicious or confused worker can read any file the user can read, use the network, and modify files outside the worktree. Container sandboxing is on the roadmap.

Everything below limits *what Taskrail itself hands to* the worker and gates. It cannot limit what the user's OS account can already do.

## Assets

| Asset | Where it lives |
|---|---|
| Provider API keys | Parent process environment only (variable named in `policy.yaml`) |
| Source code and git history | User repository and worktrees |
| Ledger and transcripts | `.taskrail/ledger.db`, `.taskrail/runs/` |
| Spend | Provider accounts |

## Trust boundaries

```
 user shell ──► taskrail (trusted) ──► provider API (trusted, TLS)
                    │
                    ├──► metering proxy, 127.0.0.1 (trusted, in-process)
                    │         ▲
                    │         │ per-run token
                    ├──► worker CLI (UNTRUSTED output, runs as user)
                    └──► gate commands (repo-defined, runs as user)
```

- **Model output is untrusted**: plans, patches, review text and anything a worker writes.
- **Repository content is untrusted** when the repo is not the user's own (benchmarks, SWE-bench tasks). Gate commands and build scripts in such repos execute code.

## Threats and v1 mitigations

| # | Threat | Mitigation in v1 | Residual risk |
|---|---|---|---|
| T1 | Worker reads or exfiltrates provider API keys | Workers get a **scrubbed environment** from an allowlist. Real keys stay in the Taskrail process; workers reach models via the local metering proxy with a **per-run random token** that is invalid after the run. Config validation rejects literal secrets in `executors.yaml`. | A worker can still read key files on disk (e.g. `~/.config/...`) that the user's account can read. |
| T2 | Runaway spend | Hard per-task (and optional per-run) USD cap enforced on every metered call. When the cap is hit, the executor's process tree is killed and the run is marked `ABORTED_BUDGET`. | Calls made by an executor that cannot be routed through the proxy are only estimated, so the cap is weaker for those executors. `doctor` warns about this. |
| T3 | Worker modifies protected code or bypasses gates | Diffs are computed from git, never from agent self-reports. Protected paths trigger escalation. Locked regions (M2) are verified by content hash. The full gate suite re-runs after each unit. | A worker could edit gate configuration in the repo; protecting `.taskrail/**` and CI config by default is planned for M1. |
| T4 | Worker hangs or forks persistent processes | Timeouts plus **process-tree kill**. Stdin is closed immediately. | Processes that fully detach (daemonize, `setsid`) can escape the tree kill. |
| T5 | Shell injection through prompts or args | Executors and gates use **argument arrays**, never shell strings. On Windows, `.cmd`/`.bat` targets get cross-spawn-style escaping, covered by a real `cmd.exe` round-trip test. Prompts go into the brief file; the command-line prompt is short and fixed. | Exotic shims that re-parse arguments more than once. |
| T6 | Secrets end up in briefs, transcripts or the ledger | Taskrail never puts key values in briefs. Transcripts go through a redaction pass for known key patterns before they are written (M1). The ledger stores token counts and costs, not content. | Secrets already present in repository files can be copied into transcripts by a worker. |
| T7 | Gates leak data over the network | Gates get no keys or proxy token. | **Network is not blocked in v1**: there is no portable way to do it without a sandbox. A gate declares `network: true` only as documentation. |
| T8 | Malicious benchmark repository | Docs warn to run untrusted tasksets only inside a VM or container. | Running `taskrail bench` on untrusted repos on a workstation is unsafe. |
| T9 | Tampered release binary | Release assets ship with `SHA256SUMS`; both installers verify checksums before installing. | Checksums come from the same release as the binary. Signing (Sigstore/cosign) and SBOMs are planned. |
| T10 | Local proxy abuse by other local processes | Binds only to 127.0.0.1 on a random port; every request requires the per-run token. | Another process running as the same user could read the token from the worker's environment. |

## Explicit non-goals for v1

- Defending against a malicious local user or other software already running as the user.
- Container, VM or seccomp sandboxing.
- Network egress control.

## User guidance

1. Run Taskrail on repositories you trust, or inside a disposable VM or container.
2. Use provider keys with spend limits set at the provider.
3. Use each agent's own permission modes; prefer modes that restrict shell access when the task allows it.
4. Keep `budget.max_usd_per_task` low while evaluating.
