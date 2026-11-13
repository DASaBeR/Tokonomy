# Tokonomy threat model (v1)

Status: reviewed for M1 (first executor adapter, metering proxy), updated for the Claude architect + Antigravity worker setup (T11-T15) for worktree escape, temp isolation and credential hardening (T16-T20; verified against `agy` 1.5.3 and Claude Code 2.1.x on Windows), and for isolated gate execution in Windows Sandbox (T16 revised, G1-G13; see [gate-isolation-research.md](gate-isolation-research.md) and [gate-isolation.md](gate-isolation.md)). Review again whenever a trust boundary changes or either CLI is upgraded: the permission behavior below is observed, not documented by the vendor.

## What Tokonomy does

Tokonomy runs coding-agent CLIs ("workers") as subprocesses inside a git worktree of the user's repository. It calls an LLM provider directly for architect work. It runs deterministic gates (build, lint, test) and records cost and outcomes in a local SQLite ledger.

## The key fact

**Git worktrees isolate changes, not execution.** Two kinds of code run during a run: the **worker CLI** (an LLM-driven program) and the **gates** (tests, builds, package scripts, which execute code the worker wrote).

- **Gates** run in a disposable **Windows Sandbox** VM when `gate_execution.provider: windows-sandbox` (the scaffolded default): a hypervisor boundary, a copy of the source, no network, no host credentials. With `host-unsafe` they run as you, as in earlier versions. There is no automatic fallback between the two; a policy without `gate_execution` refuses to run gates.
- **The worker CLI itself is not sandboxed by Tokonomy.** It runs as you, restricted by its own permission system (agy rules, T11/T14) and by everything below that limits what Tokonomy hands to it.

## Assets

| Asset | Where it lives |
|---|---|
| Provider API keys | Parent process environment only (variable named in `policy.yaml`) |
| Source code and git history | User repository and worktrees |
| Ledger and transcripts | `.tokonomy/ledger.db`, `.tokonomy/runs/` |
| Spend | Provider accounts |

## Trust boundaries

```
 user shell ──► tokonomy (trusted) ──► provider API (trusted, TLS)
                    │
                    ├──► metering proxy, 127.0.0.1 (trusted, in-process)
                    │         ▲
                    │         │ per-run token
                    ├──► worker CLI (UNTRUSTED output, runs as user, agy permission rules)
                    └──► gate commands ──► Windows Sandbox VM (UNTRUSTED code; own kernel,
                              │             no network, read-only source copy, one writable
                              │             results folder that the host validates)
                              └── host-unsafe: runs as user (explicit opt-in only)
```

- **Model output is untrusted**: plans, patches, review text and anything a worker writes.
- **Repository content is untrusted** when the repo is not the user's own (benchmarks, SWE-bench tasks). Gate commands and build scripts in such repos execute code.

## Threats and v1 mitigations

| # | Threat | Mitigation in v1 | Residual risk |
|---|---|---|---|
| T1 | Worker reads or exfiltrates provider API keys | Workers get a **scrubbed environment** from an allowlist. Credential-looking names are dropped even when allowlisted. Real keys stay in the Tokonomy process; workers reach models via the local metering proxy with a **per-run random token** (constant-time compared) that is invalid after the run. Config validation rejects literal secrets in `executors.yaml`. | A worker can still read key files on disk (e.g. `~/.config/...`) that the user's account can read. A per-run token is valid for every request during that run. |
| T2 | Runaway spend | Hard per-task (and optional per-run) USD cap enforced on every metered call. When the cap is hit, the executor's process tree is killed and the run is marked `ABORTED_BUDGET`. | Calls made by an executor that cannot be routed through the proxy are only estimated, so the cap is weaker for those executors. `doctor` warns about this. |
| T3 | Worker modifies protected code or bypasses gates | Diffs are computed from git, never from agent self-reports. Policy is loaded once from the main checkout, so edits inside the worktree cannot change the running policy. `.tokonomy/**` is always a protected path, so such edits never get committed silently. Protected paths trigger escalation. Locked regions (M2) are verified by content hash. The full gate suite runs after every attempt. Repo commit hooks are skipped when committing accepted units. | A worker can edit CI config or test files in the repo; add them to `protected_paths` if that matters. |
| T3b | The architect's edit escapes the worktree or rewrites Tokonomy state | Edits are search/replace blocks applied all-or-nothing by Tokonomy (the architect runs no commands). Paths are confined to the worktree and may not touch `.git` or `.tokonomy`; a SEARCH must match exactly once. The full gate suite and the locked-region check run after every takeover. | A bad but valid edit that passes the gates is possible; the diff is in the run's logs and the unit commit is labelled `[architect takeover]` for review. |
| T4 | Worker hangs or forks persistent processes | Timeouts plus **process-tree kill**. Stdin is closed immediately. | Processes that fully detach (daemonize, `setsid`) can escape the tree kill. |
| T5 | Shell injection through prompts or args | Executors and gates use **argument arrays**, never shell strings. On Windows, `.cmd`/`.bat` targets get cross-spawn-style escaping, covered by a real `cmd.exe` round-trip test. Prompts go into the brief file; the command-line prompt is short and fixed. | Exotic shims that re-parse arguments more than once. |
| T6 | Secrets end up in briefs, transcripts or the ledger | Tokonomy never puts key values in briefs. Everything under `.tokonomy/runs/` (transcripts, briefs, diffs, logs written by Tokonomy) goes through redaction for known key patterns, `Authorization`/`x-api-key` values and the configured provider key before it is written. The ledger stores token counts and costs, not content. | Secrets already present in repository files can be copied into transcripts by a worker. |
| T7 | Gates leak data over the network | Gates get no keys or proxy token. With `windows-sandbox` and `network_policy: none` (default) the VM has **no network adapter** (G6). | With `network_policy: full`, or with `host-unsafe`, gates can reach the network. Prepare steps have network by default (`prepare_network: full`). |
| T8 | Malicious benchmark repository | Gates, prepare steps and bench acceptance commands run through the configured gate provider (Windows Sandbox by default). | The worker CLI still runs on the host and reads the repository; a repository crafted to manipulate the worker (prompt injection in files) is not contained by gate isolation. Run untrusted tasksets in a VM. |
| T9 | Tampered release binary | Release assets ship with `SHA256SUMS`; both installers verify checksums before installing. | Checksums come from the same release as the binary. Signing (Sigstore/cosign) and SBOMs are planned. |
| T10 | Local proxy abuse by other local processes | Binds only to 127.0.0.1 on a random port; every request requires the per-run token. | Another process running as the same user could read the token from the worker's environment. |
| T11 | Antigravity worker runs shell commands you did not approve (**unless you chose "Allow all shell commands"** (`permissions.allow_all_commands`), which passes `--dangerously-skip-permissions` and accepts the risk: see config.md) | No `--dangerously-skip-permissions`, no edit mode, no wildcard rule, and **no `command(...)` rule at all by default** (`allow_gate_commands: false`). Headless `agy` cannot prompt, so every unmatched call is refused. When commands are enabled, each is one exact, whole-line literal rule; verified live that `python -c`, `git push`, `curl`, chaining (`;`, `&&`), quoting (`python -m "unittest"`), the call operator (`& ...`), `python.exe`, `cmd /c ...`, `$(...)` substitution and extra arguments are all refused while the exact line runs. `run_command` rejects a `Cwd` argument in agy 1.5.3; a reported `Cwd` outside the worktree is a fault anyway. | An enabled command that runs repository code (tests, builds) runs code the worker wrote, with your privileges (see T16). `--sandbox` is passed but its enforcement was not observable on Windows. |
| T12 | A refused or crashed worker run is reported as a finished implementation | agy returns exit code 0 and `status: SUCCESS` for refused calls, and an explicit deny rule leaves `denied_actions` empty. Tokonomy combines non-zero exit, error status, `denied_actions`, shell commands outside the allowlist, errored file tools outside the worktree **or on any path Tokonomy's own deny rules name** (added after a live run showed a refused `.git/hooks` write coming back as a clean success), and the refusal text, and **never accepts a faulted attempt**, even if the diff passes every gate. Faults retry, then escalate to an explicit, ledger-recorded architect takeover. | Detection reads agy 1.5.3's output shape; a format change degrades to `NoResult` (a fault), not to a success. Whether the model *attempts* a forbidden action varies run to run; what is guaranteed is that the attempt fails and is reported. |
| T13 | The wrong model does the work, silently | The worker's `init` event names the model the session started with and must equal the requested id exactly; an unreported model fails closed. agy rejects unknown ids itself and that is surfaced as a non-retriable `ModelMismatch`. The architect call must show the requested model among the models that produced output. A mismatch is a hard failure, never retried and never handed to the architect. | `verify_model: false` disables the worker check. The model id is what the CLI reports about itself. |
| T14 | Antigravity worker reads or writes outside the worktree | Only `read_file`/`write_file` on the worktree are granted; verified live that reads and writes outside it, `..` paths, sibling directories and the temp directory are refused, and that both agents' login folders are unreadable while agy itself stays logged in. agy's check is **lexical and follows links** (verified: it read a file outside through a junction in the worktree), so every link that leads outside is denied by its own path (verified live), and any new one fails the attempt (T17). Worktrees live outside temp by default (T18). | A link created and removed again *during* one attempt is not seen by the checks before and after it; this needs code execution, which the worker does not have by default (T16). |
| T15 | The architect's final review is trusted over the gates | The review is advisory: recorded and displayed, never able to change the run status. | A reviewer can be wrong in both directions; it only informs the human who merges. |
| T16 | Code the worker wrote is executed | The worker has no shell by default. With `windows-sandbox`, gates run in a fresh Hyper-V-isolated VM per batch with a copy of the source, no network and no host files (G1-G13 below). Gate output is redacted for credential shapes before it goes back to the worker, and gates are skipped entirely after a workspace violation. | With `host-unsafe`, **gates are not sandboxed**: a test the worker wrote runs with your privileges, can read `~/.gemini/oauth_creds.json`, use the network and print what it finds into gate output. With `windows-sandbox`, see the residual risks of G1-G13. |
| T17 | Links, the `.git` pointer or Tokonomy's own writes are used to escape the worktree | Every file Tokonomy reads or writes in a worktree (brief, config files, architect edits, files sent to the architect, locked regions) goes through `WorktreeGuard`: no `..`, no alternate data streams, no names Windows rewrites (`.tokonomy.`, `CON`), no link anywhere on the path, and on Windows the file is opened as a reparse point (never followed) and the handle is re-checked (not a link, one hard link, final path inside the worktree), which closes the check/use race for the final component. After each attempt and after the gates, new links that lead outside (or retargeted ones) are removed as links and fail the unit; a changed `.git` pointer is restored and fails the unit. All worktree git commands use explicit `--git-dir`/`--work-tree` recorded at creation, so a rewritten `.git` file cannot aim Tokonomy's `commit`/`reset` at another repository (tested), and `GIT_*` variables from the parent shell are dropped. Cleanup removes links before `git worktree remove --force`, which otherwise **follows junctions and deletes their targets** (verified). | On Linux and macOS the guard checks components immediately before each operation but cannot open with no-follow semantics, so a process running concurrently in the worktree could race it; hard-link counts are only checked on Windows. Directory components on Windows are re-validated through the final path of the opened handle rather than opened no-follow. |
| T18 | Worktrees placed where other processes or agents can reach them | Default root `%LOCALAPPDATA%\tokonomy\worktrees` (platform equivalents elsewhere), outside temp and the repository. A configured root is validated at config load and before each worktree is created, also after resolving links, and refused if it overlaps temp, the repository, the home directory itself, a drive root, or system/credential folders. `doctor` reports the effective root and leftover worktrees and removes the latter safely on request. | Other software running as you can still read the worktree root; it is not encrypted or ACL-restricted. |
| T19 | Files hidden where the diff does not look | git-ignored content is denied for writing; new ignored files created by a worker that ran no commands are deleted and fault the attempt. | Executors without a command audit (OpenCode, Claude Code) are not checked this way, because their legitimate test runs create ignored build output. |
| T20 | Credentials leak through the environment, logs or feedback | Worker and gate environments drop credential names, agent sockets and credential-bearing values (tested against Claude, Gemini, Google, GitHub, AWS, npm, database URLs, authenticated proxies). Fault details and gate feedback are redacted. The architect's Claude CLI gets no `ANTHROPIC_*` variables. | Redaction is pattern-based. Raw worker stdout/stderr logs under `.tokonomy/runs/` are written unredacted (they can contain whatever the model read); keep `.tokonomy/` out of version control (the scaffolded `.gitignore` does). |

## Gate execution threats (Windows Sandbox provider)

Gates execute code from the worktree: tests, `conftest.py`, MSBuild targets, `package.json` scripts, a `unittest.py` that shadows the standard library. The command name tells us nothing, so every gate counts as untrusted.

Status key:
- **Protected**: the boundary prevents it.
- **Partial**: reduced, with a stated residual risk.
- **Unprotected**: not addressed.
- **(verified live)**: observed on a real Windows Sandbox by the `TOKONOMY_LIVE_SANDBOX=1` suite (16/16 passed on 2026-11-10; Windows 11 Pro 26200, wsb 0.8.107). This is behavior observed on one machine, not a vendor guarantee: re-run the suite after Windows or `wsb` updates.

| # | Threat | Status | Mitigation | Residual risk |
|---|---|---|---|---|
| G1 | **Malicious test file** reads or alters host data | Protected (verified live) | The test runs inside a VM with its own kernel. The VM sees only: a read-only copy of the source, a read-only control folder, read-only toolchain folders, an optional read-only dependency archive, and one empty writable results folder. The worktree, repository, profile, temp and other drives are not mapped. | A hypervisor or Windows Sandbox escape (a vulnerability class Microsoft services; Tokonomy cannot mitigate it). |
| G2 | **Malicious build script** (MSBuild targets, `Directory.Build.props`, `setup.py`) | Protected (verified live) | Same boundary as G1: builds run inside the VM on its own copy at `C:\work`. | As G1. A build can still make its own gate report a pass (G13). |
| G3 | **Malicious package lifecycle script** (`postinstall`) | Partial | Prepare steps run in their own sandbox, on the **base commit**, before any worker runs. Gate sandboxes receive the result as an opaque archive that the host never unpacks. | With `prepare_network: full` (default) a poisoned dependency can send the **source snapshot** over the network. Lifecycle scripts the *worker* adds run in gate sandboxes without network. |
| G4 | **Credential theft** (agent logins, SSH keys, tokens, environment variables) | Protected (verified live) | Host credential files are not mapped. The VM starts from a clean image with no host user profile. Nothing from the host environment is passed in: the in-VM runner starts its commands from a fixed allowlist of OS variables (tested on the host with injected `GH_TOKEN`, `SSH_AUTH_SOCK`, `DATABASE_URL`). Toolchain folders under the profile are refused. Private package feed credentials are not forwarded. | Secrets committed to the repository are in the snapshot by definition. |
| G5 | **Host filesystem access or modifying the original worktree** | Protected (verified live) | The VM never sees the worktree. The source is a guarded copy: only files git lists, no `.git`, no `.tokonomy`, no ignored content, no file reached through a link, no hard-linked file, size-capped. The copy is mapped read-only and built from a second copy on the VM's own disk. | none known |
| G6 | **Network exfiltration** | Protected for gates with `network_policy: none` (verified live: no adapter up, no TCP, DNS or HTTPS) | `<Networking>Disable</Networking>` removes the VM's network adapter, which covers the internet, the LAN and the host's own services. | `network_policy: full` and prepare steps (G3) have NAT'd network with **no egress allowlist** (Windows Sandbox has none). |
| G7 | **Persistent background processes** | Protected (verified live) | One VM per gate batch (baseline, each attempt, each takeover check), destroyed right after. Stopping it ends every process, scheduled task and service in it. The host confirms the VM is gone (`wsb list`) before deleting its folders. | Within one batch, a process started by gate 1 can still affect gate 2 (same code, same working copy). |
| G8 | **Sandbox escape through the shared folders** | Protected (verified live) | Only the results folder is writable. Observed: inside the VM, creating a junction or symlink in the writable share fails with `Access is denied`, even as SYSTEM, and a junction aimed at a host folder did not expose it. Hard links can be made, but only between files inside the share, and the host rejects any hard-linked result file. Any link that does appear fails the batch as a security violation. | A hypervisor escape is out of scope (G1). The share's link behavior is a platform property; the live suite re-checks it. |
| G9 | **Artifact extraction attacks** (path traversal, symlinks, hard links, oversized files) | Protected | The host takes only fixed names (`N.log`, `N.report`, `N.status.json`, `deps.tar`) from the results folder. Each is opened as a reparse point (never followed) and checked: one hard link, final path inside the folder, a size cap per kind. The dependency archive is **never unpacked on the host**; only gate VMs unpack it. Unexpected entries are ignored and deleted with the folder. | none known |
| G10 | **Resource exhaustion** | Partial (memory cap and timeouts verified live) | Memory is capped per VM (`resource_limits.memory_mb`; observed: an 8 GB allocation in a 2 GB VM stopped at about 5.3 GB, memory plus the VM's pagefile). Command timeouts are enforced inside the VM (tree kill) and again by the host, which destroys the VM. Snapshot, log, report and archive sizes are capped. | **No CPU cap** (Windows Sandbox exposes none): a busy loop slows the host until the timeout. Disk use inside the VM is bounded by the VM's own disk. |
| G11 | **Dependency poisoning** | Partial | Dependencies are restored once, on the base commit, in an isolated VM. Gates then run offline against that result. | A poisoned package still runs in prepare (with network) and in every gate VM: isolation limits what it can reach, not whether it runs. Nothing pins or verifies package integrity beyond what your lock file and package manager do. |
| G12 | **Host-to-sandbox and sandbox-to-host channels** | Partial | Host to VM: read-only mappings, plus `wsb exec` command lines that Tokonomy generates. VM to host: the exec exit code and files in the results folder, all validated. Clipboard, printer, audio, video and GPU are disabled, and `ProtectedClient` is enabled. | The results folder is a real channel: gate output is fed back to the worker after redaction, which is pattern-based. |
| G13 | **Forged gate results** (a test that reports success without testing) | Unprotected | A result counts only when the in-VM status file carries the per-environment nonce and the matching command index. A pass that the exec exit code contradicts is a failure. A missing or malformed status is a failure, never a pass. | Tests are code under test, so a malicious worker can make its own tests pass. Isolation protects the host, **not the honesty of the result**. The existing mitigations still apply: baselining on the base commit, `protected_paths` for test directories, locked regions, the advisory final review, and hidden acceptance tests in benchmarks. |

**Platform-specific limitations**
- Windows Sandbox needs Windows 10/11 **Pro, Enterprise or Education**, the `wsb` CLI (24H2+), firmware virtualization, and the optional feature enabled once with admin rights and a reboot. Home edition users can only use `host-unsafe`.
- One sandbox at a time: a sandbox you opened yourself blocks gates. Tokonomy reports it and never stops it.
- `network_policy: full` / `prepare_network: full` depend on the Hyper-V Default Switch giving the sandbox a DHCP lease. On the development machine it did not (the sandbox got `169.254.x.x` only), so networked prepare steps fail there. That fails closed (no network), but it breaks dependency restore.
- `wsb exec` always exits 0 and returns no output; the command's exit code is parsed from its output. If a future `wsb` changes that format, results become `ProtocolViolation` (a failure), never a pass.
- Toolchains are the host's own installations, mapped read-only. A toolchain installed under your profile (pyenv, `%USERPROFILE%\.dotnet`, npm global CLIs in `%APPDATA%\npm`) is refused.
- The worker CLI is **not** inside the sandbox (see T11-T14 for its own isolation).

## Antigravity worker: observed permission behavior (agy 1.5.3)

These facts were established by running `agy` against a throwaway repository. The CLI's `--help` does not document them.

- Settings are passed with `--settings <file|json>` (session only, never saved) and use `permissions.allow` / `permissions.deny`. Rule kinds seen: `command(...)`, `read_file(<path>)`, `write_file(<path>)`, `read_url(<domain>)`, `mcp(<server>/<tool>)`.
- `command(...)` matches the **whole command line, literally**. Prefix rules (`command(python -m unittest)` vs `python -m unittest -q`) and regex tokens do not work.
- `deny` beats `allow`, and a deny on a directory also covers everything inside it, however specific the allow is.
- The worker's shell on Windows is PowerShell. A compound command (`a; b`) is a different command and is refused unless that exact string is granted.
- With no rule, headless mode auto-denies the call, **ends the session at that point** and still exits 0 with `status: SUCCESS`, an empty response and `denied_actions` naming only the tool (`RunCommand`, `WriteFile`). The command text appears only in the `stream-json` tool events.
- An explicit `deny` rule behaves differently: the tool step ends in `ERROR`, the model explains the refusal in prose, `status` is `SUCCESS` and `denied_actions` is absent.
- `--mode accept-edits` is not used: with it, file tools wrote outside the worktree with no rule at all. Without it, only granted paths are writable, except temp.
- `--sandbox` did not widen what is allowed (an ungranted command was still refused) but no restriction from it could be observed on Windows.
- An unknown `--model` is refused up front (`status: ERROR`, exit 1); there is no fallback to another model.
- Path rules are **lexical**. A junction inside a granted directory is followed: `read_file(<wt>\junction\secret.txt)` returned the content of a file outside the worktree. A deny rule on the junction's own path blocks it.
- Deny rules match whole path segments: denying `<wt>\.git` does not affect `<wt>\.gitignore`.
- `run_command` has no usable working-directory parameter: a `Cwd` argument fails schema validation (`additional properties 'Cwd' not allowed`), so commands run in the workspace root.
- Whether the model attempts a forbidden action is not deterministic: with the permission section visible in `AGENTS.md` (which agy loads automatically) Gemini usually declines on its own; the enforcement tests hide the brief to force the attempt.

## Security guarantees (what is enforced, and verified)

With the scaffolded `antigravity` executor and default settings:

1. The worker runs **no shell commands**; any attempt is refused by agy and fails the attempt.
2. The worker's file tools can reach **only the worktree**: not its parent, siblings, temp, the home directory, or either agent's login folder, and not through any link that leads outside.
3. The worker **cannot change** the worktree's `.git` pointer, git hooks or `.tokonomy/`; if anything does (e.g. gate code), the unit fails and Tokonomy's git commands still target the right repository.
4. A run that was refused, crashed, timed out, used another model, or tampered with the worktree is **never accepted**, whatever the gates say.
5. Tokonomy itself never reads, writes or deletes through a link inside a worktree, and its cleanup never deletes what a link points to.
6. No provider key, agent login token, SSH/GPG agent socket or credential-bearing URL is passed to the worker or the gates.

With `gate_execution.provider: windows-sandbox`, these also hold. They are unit-tested on every build, and guarantees 7-10 were observed on a real sandbox (`TOKONOMY_LIVE_SANDBOX=1`, 16/16 passed on 2026-11-10):

7. Gate and prepare commands run in a disposable VM that sees a copy of the source and nothing else of the host; they **cannot modify the worktree**.
8. Gates have **no network** unless `network_policy: full`.
9. Nothing a gate started survives its batch: the VM is destroyed and confirmed gone. Environments left by crashed runs are destroyed on the next run or by `doctor`.
10. Nothing comes back from a gate VM except validated files with fixed names; a link or an oversized file fails the unit.
11. Gates **never** fall back to the host: a missing `gate_execution` section, an unknown provider or an unavailable sandbox stops the run before any worker starts. (This one is fully tested without a sandbox.)

## Known limitations

1. **Gate isolation depends on the provider (T16, G1-G13).** With `windows-sandbox`, gate code is contained by a hypervisor boundary, but it can still forge its own results (G13), prepare steps have network by default (G3), and there is no CPU cap (G10). With `host-unsafe`, gate code runs with your privileges, as in earlier versions. The worker CLI is never sandboxed by Tokonomy.
2. **Enabling worker commands removes guarantee 1** and with it the protection against links created and removed during an attempt (T14). `doctor` warns when commands are enabled.
3. **Literal command rules only.** There is no way to allow "pytest with any arguments"; each variant must be listed, and commands containing parentheses cannot be listed at all.
4. **Detection is output-based.** It depends on agy's stream format and on the CLI's own report of which model it used. Git changes are checked independently (the diff always comes from git), which catches anything inside the worktree but not effects outside it.
5. **Linux/macOS file hardening is weaker than on Windows (T17)**: no no-follow open and no hard-link check.
6. **Subscription limits are not metered.** Worker and architect cost are recorded as estimated, so `budget.max_usd_per_task` is a weaker cap than with the metering proxy.
7. **Takeover costs the stronger model.** After repeated worker faults the architect does the work (explicitly and on the record). Set `escalation.max_takeovers_per_unit: 0` to fail the unit instead.
8. **Behaviour is observed, not documented.** Everything in the agy section above was established by testing agy 1.5.3; re-run the live suite (`TOKONOMY_LIVE=1`) after upgrading either CLI.

## Windows-specific notes

- Junctions and hard links can be created by any unprivileged process (no administrator rights or developer mode), so they are the realistic link threat on Windows and are what the tests use.
- `git worktree remove --force` follows junctions and deletes their contents; never use it on a Tokonomy worktree without removing links first (`tokonomy doctor --remove-leftover-worktrees` does this).
- Win32 rewrites `name.` and `name ` to `name` and treats `CON`, `NUL`, `COM1`... as devices; such names are refused in every path Tokonomy writes.
- Paths are compared case-insensitively; `JUNC\x` and `junc\x` are the same refused path.
- git marks the worktree `.git` file hidden; that does not protect it (an attacker clears the attribute), which is why its content is verified.

## Explicit non-goals for v1

- Defending against a malicious local user or other software already running as the user.
- Sandboxing the worker CLI itself (its own permission rules apply, T11-T14).
- Per-destination network egress allowlists (Windows Sandbox offers all-or-nothing).
- Defending against hypervisor or Windows Sandbox escapes.

## User guidance

1. Keep `gate_execution.provider: windows-sandbox`. Use `host-unsafe` only for repositories and tasks you trust, and never for benchmarks of untrusted repositories.
2. Use provider keys with spend limits set at the provider.
3. Use each agent's own permission modes; prefer modes that restrict shell access when the task allows it.
4. Keep `budget.max_usd_per_task` low while evaluating.
