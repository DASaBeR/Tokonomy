# Gate isolation: research and recommended architecture (Phase 2.5)

Status: research complete. The architecture was approved and **implemented** on 2026-11-10, and **verified live** the same day: 16/16 live tests passed on Windows 11 Pro 26200 with wsb 0.8.107. Setup, results and troubleshooting are in [gate-isolation.md](gate-isolation.md).

Where the implementation differs from the proposal below:
- The dependency archive lives in `.tokonomy/runs/<run-id>/deps/` (a folder holding only `deps.tar`, mapped read-only) instead of the gate-environment store, and is deleted at the end of the run.
- Git metadata: no bundle or `git-info.json` is provided yet. Builds that need git (SourceLink, `git describe`) will not find a repository in the sandbox.
- The in-sandbox runner also clears inherited environment variables down to an OS allowlist before starting a command.
- `wsb exec` runs commands as `System` inside the VM (no user logon needed). Inside the disposable VM that privilege does not cross the boundary, but it is not least privilege within the guest.

## 1. The problem

Gates (`prepare` steps and `gates` in `policy.yaml`) run `dotnet test`, `npm test`, `pytest`, build scripts and package lifecycle scripts. All of them execute code a worker wrote or could have influenced: test files, `conftest.py`, a `unittest.py` that shadows the standard library, `Directory.Build.props`, MSBuild targets, `package.json` scripts, `setup.py`. Today `CommandGate` runs them through `ProcessRunner` as the user, with a scrubbed environment (T16 in [threat-model.md](threat-model.md)). Environment scrubbing does not stop a test from opening `~/.gemini/oauth_creds.json`, `~/.claude/.credentials.json` or `~/.ssh/id_ed25519` directly, using the network, or writing outside the worktree.

The command name tells us nothing. `dotnet test` on a worker's tree is arbitrary code execution, so every gate counts as untrusted.

## 2. Machine this was researched on

| Item | Value |
|---|---|
| OS | Windows 11 Pro 10.0.26200 (25H2 family; `wsb` CLI requires 24H2 / 26100+) |
| Hypervisor | Running (`HypervisorPresent = True`; Docker Desktop / WSL2 use it) |
| Windows Sandbox | **Not enabled** (`WindowsSandbox.exe` absent). Enabling needs an elevated shell and a reboot |
| Hyper-V management | PowerShell module not installed; `vmcompute` stopped |
| Docker Desktop | 28.0.1 installed, engine stopped, Linux (WSL2) backend |
| Shell elevation | Not elevated |
| Hardware | 12 logical cores, 31.8 GB RAM |
| Toolchains | .NET SDK 10.0.301, Python 3.11 (`C:\Program Files\Python311`), Node (`C:\Program Files\nodejs`), Git 2.46 |

## 3. Options compared

### A. Windows Sandbox

A disposable, Hyper-V-isolated Windows VM whose base image is built from the host's own OS files. Every start is a clean image, and everything is discarded on stop.

- **Boundary:** a hypervisor (separate kernel). This is the same class of boundary as a Hyper-V VM. Microsoft documents it as a security boundary and includes it in its bounty program.
- **Lifecycle:** `.wsb` XML configuration. Since 24H2 the `wsb` CLI provides `wsb start [--config <xml>]` (returns an id), `wsb exec --id <id> -c <cmd> -r System|ExistingLogin`, `wsb share`, `wsb list`, `wsb stop --id <id>`. Microsoft's CLI reference says `exec` returns the command's exit code. A third-party project reports that `exec` returns **only** the exit code and no stdout. **Verified live (wsb 0.8.107):** `wsb exec` returns no stdout, waits for the command to finish, and **itself always exits 0**. The command's code is only in its output: `Process exited with code: 7`, or `{"ExitCode": 7}` with `--raw`. Output therefore goes to a file in the writable share, and the exit code is parsed.
- **Folder mapping:** `MappedFolders` with `ReadOnly=true`. Writes to a writable mapped folder land on the host and persist.
- **Network:** `<Networking>Disable</Networking>` removes the virtual NIC entirely. With networking on, the sandbox is NATed through the host and can reach the LAN and host services. There is no per-destination allowlist.
- **Other knobs:** `MemoryInMB`, `vGPU`, `ClipboardRedirection`, `PrinterRedirection`, `AudioInput`, `VideoInput` (all can be disabled). `ProtectedClient` adds AppContainer isolation of the *client window* on the host. No CPU limit is exposed.
- **Concurrency:** Microsoft's main page says multiple instances cannot run simultaneously. Workarounds exist but are not supported, and we do not rely on them. Tokonomy runs gates sequentially (parallel workers are out of scope), so this fits. It also means a sandbox the user opened by hand blocks Tokonomy, and the user's sandbox must never be stopped by Tokonomy.
- **Requirements:** Windows 10/11 **Pro, Enterprise or Education** (not Home), virtualization enabled in firmware, at least 4 GB RAM (8 GB recommended), 2 cores. Optional feature `Containers-DisposableClientVM` must be enabled (admin + reboot, once). Inside a VM it needs nested virtualization.
- **Toolchains:** the image contains only Windows. .NET SDK, Python, Node and Git are not present. Options: map host install folders **read-only** (fast, no network, versions identical to the host), or install per boot (slow, needs network, not reproducible). Read-only mapping is the practical choice.
- **Performance (expected, to be measured):** cold start of roughly 10-30 s on SSD hardware. Mapped-folder I/O is slower than local disk, so the source is copied to the sandbox's own disk before building.

### B. Hyper-V virtual machines

- **Boundary:** a hypervisor, the strongest option and equal to A.
- **Lifecycle:** create from a golden VHDX plus a differencing disk or checkpoint, revert after each use. Exec via PowerShell Direct (`Invoke-Command -VMName`), which needs guest credentials. Transfer via `Copy-VMFile` (Guest Services) or PowerShell Direct sessions.
- **Network:** a VM with no network adapter, or a private switch. Allowlisted egress needs a proxy VM or firewall configuration.
- **Costs:** needs the Hyper-V role and **admin or Hyper-V Administrators** membership for every operation. Someone has to build and maintain a licensed Windows guest image (patching, toolchain installs, activation), plus 20-40 GB of disk per golden image. Checkpoint revert is fast (seconds). A cold boot is 30-90 s.
- **Verdict:** same boundary as Windows Sandbox with far more operational burden (image lifecycle, licensing, credentials, admin group). Worth keeping as a future provider for CI or headless servers where Sandbox is unavailable (Server SKUs), not as the default for a developer workstation.

### C. Containers

**Windows containers (Server Core / Nano Server).** On Windows client, Docker uses Hyper-V isolation by default, which gives a VM boundary. Process isolation shares the host kernel, and container escapes have a long CVE history. Requirements: Docker Desktop switched to Windows-containers mode (a global engine switch that breaks the user's Linux containers while active), images of 2-5 GB, and toolchain images to maintain. Nano Server cannot run most SDKs. Docker Desktop requires a paid subscription in larger organizations.

**Linux containers via WSL2 / Docker Desktop.** All containers share one utility VM's Linux kernel, so the boundary is Linux namespaces/cgroups/seccomp. The Docker Desktop VM can reach the host drives (`/mnt/host/c`), so a container escape reaches the user's whole filesystem with the user's rights. Hardening ("Enhanced Container Isolation") is a paid feature. Projects that target Windows (WPF, WinForms, Windows-only tests, `.cmd` scripts) cannot run there at all. Network policy, cgroup memory/CPU limits and `--rm` lifecycle are excellent.

**Verdict:** Linux containers give the best resource controls and egress tooling but a weaker boundary, and they cannot build Windows-targeted code, so they cannot be the default for a Windows-first tool. A `docker` provider (Linux, `--network none`, no bind mounts except a read-only snapshot) is a reasonable *later* addition for cross-platform repositories and for Linux and macOS hosts.

### D. Dedicated low-privilege Windows account

- **Boundary:** same kernel, different security principal. It stops reads of the developer's profile (profiles are ACL'd to their owner) and credential stores (DPAPI, Credential Manager are per-user).
- **Problems:** creating the account and setting ACLs needs admin. The account is still a member of `Users`, which can read much of the disk (other repositories on `D:\` are usually readable by `Users`/`Authenticated Users` by default, and this repo's drive is one example). Network cannot be limited per user without WFP programming (Windows Firewall rules cannot target a local user directly). Launching as another user needs its password or a service (`CreateProcessWithLogonW`, a stored secret). Kernel and LPE exploits break it. ACL drift over time is a maintenance problem.
- **Verdict:** rejected as the primary boundary. It mitigates credential theft but not host filesystem reads or the network, and it costs nearly as much admin setup as Windows Sandbox.

### D′. AppContainer / Less-Privileged AppContainer (considered in addition)

Chromium's and Edge's sandbox mechanism, and a mature Windows facility: a process token with an AppContainer SID, created **without admin** (`CreateAppContainerProfile`).

- **Filesystem:** denied everything except objects whose ACL grants the container SID or `ALL APPLICATION PACKAGES` (System32, Program Files). LPAC removes even that. The user profile, `.ssh`, `.claude`, `.gemini` and other repositories are unreadable unless explicitly granted.
- **Network:** denied unless the `internetClient` / `privateNetworkClientServer` capabilities are granted. Loopback to host services is blocked by default.
- **Process:** combined with a **job object** (`KILL_ON_JOB_CLOSE`, no breakaway, memory and CPU-rate limits) it gives reliable descendant termination and resource limits.
- **Weaknesses:** same kernel (a kernel/LPE bug escapes). Tools must cope with a restricted profile: .NET, NuGet, npm and Python need writable home and cache directories redirected into the granted area, and some toolchains misbehave. ACL grants on the snapshot directory must be added and removed carefully.
- **Verdict:** clearly stronger than D, weaker than A/B, and needs no admin, no reboot and works on Home. It is a credible **second provider** for machines without Windows Sandbox, but it is not a hypervisor boundary and must be labelled as such.

## 4. Comparison

| | Windows Sandbox | Hyper-V VM | Windows container (Hyper-V iso.) | Linux container (WSL2) | Low-priv account | AppContainer + job |
|---|---|---|---|---|---|---|
| Boundary | Hypervisor | Hypervisor | Hypervisor | Shared Linux kernel in a VM that sees host drives | Same kernel, ACLs | Same kernel, capability/ACL deny-by-default |
| Host credentials reachable | No (not mapped) | No | No | No unless mounted; escape → yes | Partly (Users-readable files) | No unless granted |
| Other repos reachable | No | No | No | No (escape → yes) | Often yes | No |
| Network off | Yes, NIC removed | Yes | Yes | Yes | No (needs WFP) | Yes, default |
| Egress allowlist | No | Via proxy/switch | Via proxy | Via network config | No | No (all or nothing) |
| Memory limit | Yes | Yes | Yes | Yes | Job object | Job object |
| CPU limit | No | Yes | Yes | Yes | Job object | Job object |
| Descendant kill | Stop VM | Stop/revert VM | Stop container | Stop container | Job object | Job object |
| Runs Windows toolchains | Yes (host-mapped) | Yes (image) | Yes (image) | **No** | Yes | Mostly, needs redirections |
| Admin needed | Once (feature) | Always (or HV Admins) | Docker install + engine switch | Docker install | Setup + ACLs | **No** |
| Edition | Pro/Ent/Edu | Pro/Ent/Edu/Server | Pro+ | any w/ WSL2 | any | any |
| Startup per env | ~10-30 s (to measure) | 5-90 s | 5-20 s | 1-3 s | <1 s | <1 s |
| Ops burden | Low | High (images, licensing) | High (images, engine switch) | Medium | Medium-high (ACL drift) | Medium (profile redirection) |
| Concurrency | **1 instance** | Many | Many | Many | Many | Many |

## 5. Recommendation

**Primary provider: Windows Sandbox**, one fresh sandbox per gate batch, networking disabled, the source delivered as a read-only snapshot, and host toolchains mapped read-only.

Reasons:
1. It is the only option that gives a **hypervisor boundary** on a stock Pro workstation without maintaining guest images. Security is prioritized over speed, as the brief requires.
2. Clean state per boot removes persistence (background processes, planted files) between attempts *by construction*, which no same-kernel option can guarantee.
3. Networking off removes the virtual NIC, which is stronger than any firewall rule and covers the LAN and host services as well as the internet.
4. Stopping the VM terminates every descendant, including daemonized ones (closes T4's residual risk for gates).
5. It runs Windows-targeted builds and tests, which Linux containers cannot.

Rejected as default:
- **Hyper-V VM:** same boundary, much higher operational cost. Kept as a possible future provider for servers and CI.
- **Containers:** Linux cannot run Windows targets and is a weaker boundary on Docker Desktop. The Windows-container engine switch and image maintenance are disproportionate. Possible future `docker` provider for cross-platform repos.
- **Low-privilege account:** does not block other-repository reads or the network, and needs admin anyway.
- **AppContainer:** a good no-admin fallback, but a same-kernel boundary. Proposed as an **optional second provider**, explicitly selected, never an automatic fallback.

**No automatic fallback in any direction.** If the configured provider is unavailable, the run stops before the worker starts, with a diagnostic. `host` execution exists only under an explicit, alarmingly named value (`host-unsafe`) for development and tests.

## 6. Proposed architecture

### 6.1 Abstraction (kept small)

```csharp
// Tokonomy.Runtime.Gates.Isolation
public interface IGateExecutionProvider
{
    string Name { get; }                                   // "windows-sandbox", "host-unsafe", ...
    bool IsIsolated { get; }                               // false only for host-unsafe
    Task<ProviderStatus> CheckAsync(CancellationToken ct); // used by doctor and before a run starts
    Task<IGateEnvironment> CreateAsync(GateEnvironmentRequest request, CancellationToken ct);
}

public interface IGateEnvironment : IAsyncDisposable      // dispose = destroy, always idempotent
{
    Task<GateCommandResult> RunAsync(GateCommand command, CancellationToken ct);
}

public sealed record GateEnvironmentRequest(
    SourceSnapshot Source,              // read-only exported tree (see 6.2)
    string? PreparedDependencies,       // read-only prepared tree from `prepare`, or null
    NetworkPolicy Network,              // None | Full (Full only for prepare, or when explicitly configured)
    ResourceLimits Limits);             // memory MB, timeout, output caps

public sealed record GateCommand(string Name, IReadOnlyList<string> Args, string? ReportPath, TimeSpan Timeout);

public sealed record GateCommandResult(
    int? ExitCode, GateTermination Termination,  // Completed | TimedOut | Cancelled | EnvironmentFailed
    string OutputTail,                           // capped, redacted
    string? ReportFile,                          // validated copy on the host, or null
    TimeSpan Duration, string? FailureDetail);
```

Errors:
- `GateIsolationUnavailableException`: the provider cannot run here. The run stops before any worker starts.
- `GateEnvironmentException`: provisioning, transfer or teardown failed. The attempt is recorded as an **infrastructure fault**, the run aborts, and the worker is not blamed or retried against it.
- `GateSecurityException`: the snapshot or artifacts contained a link, hard link, traversal, a forbidden name or an oversize file. Same severity as `WorkspaceViolation`: unit fails, never retried, never handed to the architect.

`CommandGate` keeps its baselining logic and asks an `IGateEnvironment` to run the command instead of `IProcessRunner`. The orchestrator creates one environment per **gate batch** (baseline; each attempt; each takeover re-check) and disposes it right after. `HostUnsafeGateProvider` wraps today's `ProcessRunner` path, so existing E2E tests keep working by selecting it explicitly.

### 6.2 Source snapshot (host side, before the sandbox exists)

1. Enumerate the worktree with `git ls-files -z --cached --others --exclude-standard` (tracked plus new non-ignored files) using the explicit `--git-dir`/`--work-tree` pair.
2. Copy each file with the existing `WorktreeGuard` open rules: no link anywhere on the path, opened as a reparse point and never followed, one hard link, final path inside the worktree, no ADS or device names. **Any violation fails the batch** (`GateSecurityException`), because the worktree checks already run before gates and a link at this point means something changed under us.
3. Never copied: `.git`, `.tokonomy`, git-ignored content (`bin/`, `obj/`, `node_modules/`), the brief file's Tokonomy section (restored before gates already).
4. The snapshot lives in `%LOCALAPPDATA%\tokonomy\gate-envs\<env-id>\src`, outside temp and outside the repository, validated with the same rules as `WorktreeRoot`.
5. Git metadata: the sandbox gets **no `.git`**. Projects whose build needs git (SourceLink, `git describe` versioning) get a generated read-only `git-info.json` (commit, branch, diff stats) and, if opted in, a **bare `git bundle` of the base commit only** that the sandbox can clone into a throwaway repository. It is never a path back into the real repository.
6. Size and count limits (`artifact_limits.max_snapshot_mb`, default 512 MB) stop a worker from turning the snapshot into a disk-filling attack.

Inside the sandbox the runner copies the read-only `src` to the sandbox's own `C:\work` and builds there. The host worktree is never mapped, so **gates cannot modify it** regardless of what runs.

### 6.3 Windows Sandbox provider

Mapped folders (all on host under `gate-envs\<env-id>\`):

| Sandbox path | Host source | Mode |
|---|---|---|
| `C:\tk\src` | snapshot | read-only |
| `C:\tk\deps` | prepared dependencies (if any) | read-only |
| `C:\tk\ctl` | runner script + per-batch nonce + command list | read-only |
| `C:\tk\tools\<name>` | host toolchain dirs listed in `gate_execution.toolchains` (e.g. `C:\Program Files\dotnet`) | read-only |
| `C:\tk\out` | empty results folder | **writable** (the only one) |

Configuration: `Networking=Disable` (unless policy says otherwise), `vGPU`, clipboard, printer, audio and video all `Disable`, `MemoryInMB` from limits, `ProtectedClient=Enable`.

Execution: `wsb start --config <xml>` → record `{env-id, sandbox-id, pid, run-id}` in `gate-envs\<env-id>\state.json` → wait for readiness → per command `wsb exec -r System -c "powershell -NoProfile -File C:\tk\ctl\run.ps1 <n>"` → the runner runs the argument array with a clean environment, writes `out\<n>.log` (capped) and copies the declared report to `out\<n>.report.xml` → the exit code from `wsb exec` on the host side is authoritative. A host timer calls `wsb stop` on timeout or cancellation, which kills everything in the VM.

Artifact collection (host side): enumerate `out` without following anything, accept only regular files with expected names (`<n>.log`, `<n>.report.xml`), refuse reparse points, hard links > 1, ADS and unexpected names, enforce per-file and total size caps, copy into `.tokonomy/runs/<run-id>/gates/`, and redact before showing anything to the worker. Everything else in `out` is ignored and deleted with the link-safe delete.

**Honesty about results:** a sandbox stops gate code from *harming the host*. It cannot make gate code *honest*. The tests are code under test, so a malicious worker can make them print "passed" or write a passing JUnit file. This is not new (it is true on the host too). The existing defenses still apply: the baseline runs before the worker on the base commit, protected paths cover test directories when configured, locked regions, the advisory final review, and hidden tests in the benchmark harness. The threat model will say so explicitly.

### 6.4 Prepare and dependencies

`prepare` (e.g. `npm ci`, `dotnet restore`) runs in its **own** sandbox, on the **base commit** snapshot (before any worker runs), with the network policy `prepare_network` (default `full` for prepare only, since restoring needs it, and it can be set to `none` for offline repos). Its output tree (e.g. `node_modules`, `obj/project.assets.json`, a NuGet package folder) is copied out of the sandbox to `gate-envs\<run>\deps` **without** following links. pnpm's symlinked layout is kept as links in that host folder, but the host never reads through them and only ever maps the folder read-only into gate sandboxes. Deletion uses the existing link-first cleanup. Gate sandboxes then run with **no network**, using the prepared tree and, for NuGet, `--no-restore` or a local packages folder.

Credentials: authenticated feeds (`nuget.config` with credentials, `.npmrc` tokens) are **not** forwarded in phase 2.5. Prepare runs with public sources only. Private feeds are a documented limitation, with a future design to restore on the host with lifecycle scripts disabled (`npm ci --ignore-scripts`) and hand over packages only.

Lifecycle scripts in dependencies run during prepare inside the sandbox, with network but no host access and no credentials. A poisoned dependency can still exfiltrate the *source snapshot* over that network. That is documented as partially mitigated.

### 6.5 Configuration (proposal)

```yaml
gate_execution:
  provider: windows-sandbox        # windows-sandbox | host-unsafe   (appcontainer: optional, see question 2)
  network_policy: none             # none | full   (gates)
  prepare_network: full            # none | full   (prepare only, base commit)
  timeout_seconds: 900             # per command; per-gate timeout_seconds still overrides
  startup_timeout_seconds: 120
  resource_limits: { memory_mb: 4096 }
  artifact_limits: { max_file_mb: 16, max_total_mb: 64, max_snapshot_mb: 512 }
  cleanup_policy: always           # always | keep-on-failure (keeps host-side folders only; the VM is always destroyed)
  toolchains:                      # host folders mapped read-only; added to PATH inside the sandbox
    - "C:/Program Files/dotnet"
    - "C:/Program Files/Python311"
```

### 6.6 Lifecycle and recovery

- `state.json` is written **before** `wsb start`. On the next start, or in `doctor`, any env whose recorded run is no longer running gets `wsb stop` (only ids Tokonomy recorded, never a user's sandbox) and its folder removed with the link-safe delete.
- One sandbox at a time: if `wsb list` shows a sandbox Tokonomy did not create, the batch fails with "close your Windows Sandbox window" and never touches it.
- Disposal happens on every path (`await using`), including exceptions and cancellation. Stop failures are reported as a leftover environment, never ignored.
- `doctor` checks: edition, feature enabled, `wsb` present and version, a trial start/exec/stop with timing (opt-in `--probe-sandbox`), that toolchain paths exist and are not inside the home directory, leftover envs, and that the provider is not `host-unsafe` (warn loudly if it is).

## 7. Known limitations

Confirmed on 2026-11-10, with the live suite at 16/16:
- Boot is fast: `wsb start` about 1.2 s, accepting commands at about 1.5 s, `stop` about 0.5 s. A full Tokonomy environment start takes 7.3-7.8 s.
- Files the host adds to a mapped folder after boot become visible inside the VM only after a short delay, so Tokonomy passes each command's spec on the `exec` command line instead.
- From inside the VM, the writable share refuses junctions and symlinks (`Access is denied`) but allows hard links between its own files.
- Networking (`Default`, `Enable` or omitted) depends on the Hyper-V Default Switch handing out a DHCP lease. On the development machine it did not, and the sandbox got only `169.254.x.x`.

The original list of expected limitations follows.

1. Windows Sandbox needs Pro/Enterprise/Education, the optional feature enabled with admin and a reboot, and virtualization. Home edition users can only use `host-unsafe` (or AppContainer, if added).
2. Single instance: a user's own open sandbox blocks gates. Gates are sequential.
3. No CPU limit. Memory is capped by `MemoryInMB`.
4. Start-up cost per gate batch (to be measured). A unit with three attempts pays it 3-4 times.
5. `wsb exec` output capture (to be verified). The design does not depend on it.
6. Network is all or nothing, with no egress allowlist. Prepare-with-network can leak the snapshot.
7. Private package feeds are unsupported in isolated mode.
8. Toolchains come from the host read-only. A project needing a toolchain not installed on the host, or one installed under the user profile (e.g. `%USERPROFILE%\.dotnet`, pyenv), must install it to a non-profile location; profile paths are refused because mapping them would expose the profile.
9. Gate results remain controllable by the code under test (6.3).

## 8. Sources

- Windows Sandbox overview: https://learn.microsoft.com/windows/security/application-security/application-isolation/windows-sandbox/
- Windows Sandbox CLI (`wsb`): https://learn.microsoft.com/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli
- `.wsb` configuration: https://learn.microsoft.com/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file
- Windows Sandbox FAQ: https://learn.microsoft.com/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-faq
- Third-party report on `wsb exec` returning only the exit code: https://github.com/gerneio/WindowsSandboxPlayground
