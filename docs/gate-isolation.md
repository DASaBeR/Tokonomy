# Gate isolation: setup, architecture, verification, troubleshooting

Gates (tests, builds, linters), prepare steps and benchmark acceptance commands execute code that a worker wrote. Tokonomy runs them in a disposable **Windows Sandbox** VM, so that code cannot reach your files, your credentials or the network. Why Windows Sandbox and not Hyper-V VMs, containers or a separate account: [gate-isolation-research.md](gate-isolation-research.md). The threats it does and does not cover are G1-G13 in [threat-model.md](threat-model.md). Configuration reference: [config.md](config.md#gate-execution).

## 1. Setup

**Requirements**

| | |
|---|---|
| Windows | 10/11 **Pro, Enterprise or Education**, version 24H2 (build 26100) or later for the `wsb` command line. Not available on Home. |
| Hardware | Virtualization enabled in firmware (VT-x/AMD-V), at least 4 GB RAM (8 GB+ recommended; each gate VM gets `resource_limits.memory_mb`, 4096 by default), SSD recommended. |
| Inside a VM | Nested virtualization must be enabled on the outer VM. |
| Toolchains | Installed machine-wide (e.g. `C:\Program Files\dotnet`, `C:\Program Files\Python311`, `C:\Program Files\nodejs`). Installs under your profile are refused. |

**Enable Windows Sandbox (once, needs admin and a reboot)**

```powershell
# elevated PowerShell
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
# reboot, then in a normal shell:
wsb list          # must run without error
```

**Configure the repository**

`tokonomy init` writes this for new repositories. Add it to existing policies:

```yaml
gate_execution:
  provider: windows-sandbox
```

Then run `tokonomy doctor`. Its *gate isolation* rows should show `ok` for `windows-sandbox`, one row per mapped toolchain, and no leftover environments.

## 2. How it works (as implemented)

```
RunOrchestrator ──► IGateExecutionProvider            (GateExecutionFactory: config → provider, no fallback)
                     ├─ WindowsSandboxProvider        (wsb start / exec / stop, GateEnvStore for recovery)
                     └─ HostUnsafeGateProvider        (explicit opt-in: the pre-2.5 behavior)
CommandGate ──► IGateEnvironment.RunAsync(GateCommand) → GateCommandResult
```

Per run:

1. **Before the worktree exists**, the provider is resolved and checked. A missing section, an unknown provider, a refused toolchain or `wsb` not being available stops the run with a diagnostic. Environments left behind by crashed runs are cleaned up at this point.
2. **Prepare** (if any) runs in its own VM on the base commit, with `prepare_network`. Its output (new files plus package caches) is packed into `deps.tar` inside that VM. The host stores the archive under `.tokonomy/runs/<id>/deps/`, never unpacks it, and deletes it at the end of the run.
3. **Every gate batch** (the baseline, each attempt, each takeover check):
   - The host copies the files git lists from the worktree through `WorktreeGuard`: no link anywhere on the path, opened without following, one hard link, no `.git` and no `.tokonomy`, size-capped.
   - The copy goes to `%LOCALAPPDATA%\tokonomy\gate-envs\<env>\src`, and `state.json` is written there before anything starts.
   - The host starts a VM with these mappings, all read-only except `out`: `src`, `ctl` (runner script and command specs), `deps` (if any), the toolchains, and the empty writable `out`. Networking, GPU, clipboard, printer, audio and video are off.
   - Inside the VM, `run.ps1 -Mode setup` unpacks `deps.tar` and copies `src` to `C:\work`. Each gate then runs as `run.ps1 -Mode run -Index N`: a fixed environment, the command line rebuilt from the argument array, the timeout enforced with a tree kill, and `N.log`, `N.report` and `N.status.json` written to `out`.
   - The host validates `out` (no links; fixed names only; size caps; the nonce must match). It then stops the VM, confirms with `wsb list` that it is gone, and deletes the folders without following links.
4. **Locked-region checks** stay on the host. They read files through the guard and execute nothing.

What a gate result means: `Completed` with exit 0 is a pass. `TimedOut`, `NotFound`, a non-zero exit and `ProtocolViolation` (missing or forged status, or a status that contradicts the exit code) are all failures. A link or hard link in the source or in `out` raises `GateSecurityException`. That is a workspace violation: the unit fails at once and is not retried or handed to the architect. Provisioning failures raise `GateEnvironmentException`, which stops the run with an error and does not blame the worker.

## 3. Verifying isolation on your machine

The unit suites run everywhere. They cover the provider protocol with a scripted `wsb`, real junction and hard-link attacks on the snapshot and results folder, and the real in-VM runner script, executed by Windows PowerShell 5.1 on the host against a throwaway folder. The **live** suite boots real sandboxes and attacks them from the inside with harmless probes:

```powershell
$env:TOKONOMY_LIVE_SANDBOX = "1"
dotnet test tests/Tokonomy.Runtime.Tests --filter "FullyQualifiedName~LiveSandboxTests" --logger "console;verbosity=detailed"
```

| Live test | Claim it checks |
|---|---|
| `The_provider_is_available_boots_runs_a_command_and_leaves_nothing_behind` | Exit codes and output come back; no VM and no folder is left |
| `Host_credentials_the_profile_and_other_repositories_are_unreachable` | The host's `~`, `.ssh`, `.claude`, `.gemini`, `%APPDATA%`, the worktree, the repository and temp are not visible (G1, G4, G5) |
| `Gates_cannot_modify_the_worktree_or_the_read_only_snapshot` | Writes to the snapshot are refused; changes in `C:\work` never reach the host (G5) |
| `The_snapshot_has_no_git_metadata_or_tokonomy_state` | No `.git` or `.tokonomy` inside |
| `With_network_none_outbound_and_local_connections_fail` | TCP and HTTPS to the internet and to host addresses fail (G6) |
| `Background_processes_do_not_survive_the_environment` | A detached process and an ONSTART scheduled task die with the VM (G7) |
| `A_command_past_its_timeout_is_killed_and_the_environment_keeps_working` / `Cancellation_destroys_the_sandbox` | Timeouts and cancellation terminate everything (G10) |
| `Memory_is_capped_by_the_configured_limit` | An 8 GB allocation in a 2 GB VM fails inside the VM (G10) |
| `A_junction_made_inside_the_results_folder_does_not_reach_host_files` | A guest-made junction in the writable share does not resolve to host files (G8) |
| `Links_cannot_be_made_in_the_results_folder_and_a_hard_linked_report_is_rejected` | Junctions and symlinks cannot be created in the share; a hard-linked result file is rejected (G8, G9) |
| `A_sandbox_left_by_a_crashed_run_is_found_stopped_and_removed` | Crash recovery |
| `Host_toolchains_mapped_read_only_run_inside_python_dotnet_and_node` | Compatibility with read-only host toolchains |
| `Wsb_exec_waits_for_a_long_command_and_reports_its_exit_code` | `wsb exec` blocks until the command ends and reports its exit code |
| `A_full_run_executes_every_gate_in_the_sandbox_and_accepts_the_fix` | A real orchestrated run (git, worktree, baseline, attempt, commit) with every gate in Windows Sandbox |
| `With_network_full_the_policy_is_applied_too` | Informational: whether `full` actually reaches the internet on this host |

**Results (2026-11-10, Windows 11 Pro 26200, wsb 0.8.107): 16 of 16 passed**, no sandbox left running. Observed:
- Host paths probed from inside (profile, `.ssh`, `.claude`, `.claude.json`, `.gemini`, `oauth_creds.json`, `.git-credentials`, `%APPDATA%`, worktree, repository, temp): **none visible**.
- Writing to the read-only snapshot: `Access to the path 'C:\tk\src\victim.txt' is denied`. The worktree is unchanged.
- `network_policy: none`: no TCP, DNS or HTTPS, **0 network adapters up**.
- Inside the VM, `mklink /J` and `mklink /D` into the writable share fail with `Access is denied`, even as SYSTEM. A junction aimed at a host folder did not expose it. `mklink /H` works between files inside the share; the host rejected the result (`it is hard-linked (2 names)`).
- A detached process and an ONSTART scheduled task: gone with the VM. The host process count was unchanged.
- An 8 GB allocation in a 2 GB VM stopped at about 5.3 GB inside the VM (memory plus the VM's pagefile). The host was unaffected.
- A 600 s command was cancelled after 10 s, the VM was destroyed, and the next command got a fresh VM.
- Python 3.11, .NET SDK 10 and Node, mapped read-only from `C:\Program Files`, all ran.
- **`network_policy: full` did not reach the internet on this host**, although the host itself does. The sandbox adapter got only a self-assigned `169.254.x.x` address (no DHCP from the Hyper-V Default Switch). This is a host networking problem (see Troubleshooting). Until it is fixed, prepare steps that download packages fail here.

Issues the live run found and that are now fixed:
1. `wsb exec` exits 0 whatever the command does; the command's code is only in its output. The provider now parses it.
2. A command spec written to the control folder right before `exec` could be invisible inside the VM. Specs now travel in the exec command line.
3. The availability check failed when the gate-environment folder did not exist yet.
4. Destroying an environment that recovery had already removed threw.

## 4. Measurements

Development machine: 12 logical cores, 32 GB, SSD, Windows 11 Pro 26200, wsb 0.8.107.

| Step | Cost | How measured |
|---|---|---|
| `wsb start` | ~1.2 s | manual probe |
| Start until the VM accepts `exec` | ~1.5 s | manual probe |
| `wsb stop`, confirmed by `wsb list` | ~0.5 s | manual probe |
| Environment startup as Tokonomy does it (snapshot, boot, readiness, setup copy) | **7.3-7.8 s**, every batch | `gate_env.startup_ms` in `transcript.jsonl`; 14 live runs |
| A trivial gate inside the VM | ~5 s (first Windows PowerShell start in a fresh image dominates) | live suite `command ...s` |
| `python -m unittest` / `dotnet --info` / `node -e` | 3.3 s / 4.4 s / 2.9 s | live suite |
| Full run: baseline + one attempt, one Python gate each | **23.7 s** end to end | `A_full_run_...` |
| Guarded source snapshot | ~130 ms for this repository (192 files, 1.1 MB) warm: roughly 0.7 ms per file | Release build, `SourceSnapshot.Copy` |
| Runner overhead, measured on the host | ~360 ms | `run.ps1 -Mode run`, 5 runs |

Rule of thumb: about **8 s per gate batch plus about 5 s for each gate**, on top of what the gate itself takes. A unit with three attempts pays the batch cost four times (baseline + 3). That is the price of a fresh VM per batch, which guarantees nothing carries over between attempts.

## 5. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `gate isolation unavailable: policy.yaml has no gate_execution section` | Choose a provider (section 1). `host-unsafe` restores the old behavior knowingly. |
| `` `wsb` was not found `` | The feature is off, Windows is older than 24H2, or this is a Home edition. Enable the feature and reboot. |
| `another Windows Sandbox is running (...)` | Windows Sandbox runs one instance at a time. Close your sandbox window. If it was left by a crashed Tokonomy run: `tokonomy doctor --remove-leftover-gate-environments`. |
| `cannot prepare the gate environment: 'X' was not found on this machine` | The gate's executable is not on the host's `PATH`, so there is nothing to map. Install it, or list its folder in `gate_execution.toolchains`. |
| `toolchain folder ... overlaps your user profile` | The tool is installed per user. Install it machine-wide, or pick a folder outside the profile. |
| `'X' was not found inside the gate environment` | The executable lives somewhere not mapped (for example a global npm CLI). Use `npx`/a local dependency, or map its install folder. |
| `did not accept commands within Ns` | Slow boot. Raise `startup_timeout_seconds`, close memory-hungry applications, or lower `resource_limits.memory_mb`. |
| Gate fails only in the sandbox: missing packages | Restore in `prepare` and run gates offline (`--no-restore`, a venv, `npm ci`). See [config.md](config.md#gate-execution). |
| Gate fails only in the sandbox: needs the network | Set `network_policy: full` only if you accept exfiltration risk (G6). Better: fetch in `prepare`. |
| Prepare fails to download (`npm ci`, `dotnet restore`) although `prepare_network: full` | The sandbox may get no DHCP lease from the Hyper-V Default Switch. Check from a test sandbox: an address of `169.254.x.x` means no lease. Common causes are a third-party firewall or antivirus filtering `vEthernet (Default Switch)`, a VPN client, or stale Host Network Service state. Allow the adapter in your firewall, or (as admin) restart the `hns` service, or reboot. Seen on the development machine; not a Tokonomy setting. |
| `wsb ip` times out | Same cause: the sandbox has no IP address. |
| `workspace violation: gate environment: ... link(s) in its results folder` | Code under test created a link in the shared folder. This is treated as an attack; inspect the attempt's diff. |
| A batch's folders stayed in `gate-envs` | The VM could not be confirmed stopped (folders are never deleted under a live VM), or `cleanup_policy: keep-on-failure`. Run `tokonomy doctor --remove-leftover-gate-environments`. |
| Need to see what the VM saw | `cleanup_policy: keep-on-failure` keeps `src`, `ctl` and `out` of failed batches. Gate logs are always in `.tokonomy/runs/<id>/logs/`. |

**Log retention.** Gate logs (tail-capped at `max_log_kb`) and collected reports are written to `.tokonomy/runs/<id>/logs/` and stay until you delete the run folder. They are raw command output: redaction is applied only to the tail fed back to the worker. `.tokonomy/` is git-ignored by the scaffold. Environment folders under `gate-envs` are deleted after each batch unless kept as described above.

## 6. Production readiness

| Area | State |
|---|---|
| Fail-closed configuration, no host fallback | **Ready.** Enforced in one place (`GateExecutionFactory` + orchestrator pre-check) and tested end to end. |
| Snapshot and artifact handling (links, hard links, traversal, size limits) | **Ready.** Built on the existing `WorktreeGuard` no-follow primitives; tested with real junctions and hard links. |
| Result protocol (nonce, status/exit agreement, fail-safe defaults) | **Ready** for what it can promise. It cannot stop tests from forging their own success (G13). |
| Lifecycle (per-batch VMs, timeouts, cancellation, crash recovery) | **Ready.** Verified live against wsb 0.8.107, and the unit tests' scripted `wsb` now behaves like the real one. |
| Isolation claims G1, G4-G8 | **Verified live** on one machine (section 3). Re-run the live suite after Windows or `wsb` updates; the behavior is observed, not documented by Microsoft. |
| Dependencies (`deps.tar` round trip) | Script logic tested on the host. **Not yet exercised live**, because sandbox networking does not work on the development machine. Large `node_modules` trees make export slow (Windows PowerShell enumeration). Private feeds are unsupported. |
| Performance | Measured: ~8 s per gate batch + ~5 s per gate. Acceptable for supervised runs, noticeable for benchmarks. |

**Assessment:** gate isolation is **ready for supervised coding tasks involving untrusted agent code on Windows**, with network-off gates, which is the default. The boundary was observed to hold on this machine for every attack in the suite: credential and file reads, worktree writes, network, persistence, links, memory. The fail-closed behavior and the host-side defenses are tested on every build. Not yet production-ready:
- **network-dependent prepare steps.** They are blocked on this machine by host networking and are untested live.
- **results that gate code can forge** (G13). This is inherent; isolation protects the host, not the honesty of the result.
- **the unlimited CPU** (G10).
