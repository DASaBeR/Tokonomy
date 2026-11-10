using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tokonomy.Core.Config;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Gates.Isolation;

/// <summary>
/// Runs gates in Windows Sandbox: a disposable Hyper-V virtual machine (its own kernel) that starts from a clean image
/// every time. One sandbox per environment, controlled with the <c>wsb</c> CLI (Windows 11 24H2+).
/// <list type="bullet">
/// <item>Mapped folders: the source snapshot, the control folder, the dependency archive and toolchains are <b>read-only</b>;
/// a single empty results folder is writable. The worktree, the repository, the profile and temp are never mapped.</item>
/// <item>Networking is disabled (no virtual NIC) unless the policy says otherwise; clipboard, printers, audio, video and the GPU are off.</item>
/// <item>Stopping the sandbox ends every process in it, so timeouts, cancellation and cleanup cannot leave anything running.</item>
/// <item>Before starting, the environment is recorded in <see cref="GateEnvStore"/>, so a crashed run's sandbox is stopped
/// and its folders removed later (<see cref="RecoverAsync"/>). Sandboxes Tokonomy did not start are never touched.</item>
/// </list>
/// </summary>
public sealed partial class WindowsSandboxProvider(
    IProcessRunner runner, GateExecutionConfig config, GateEnvStore store, IReadOnlyList<Toolchain> toolchains,
    string wsbCommand = "wsb", TimeSpan? pollInterval = null, TimeSpan? commandGrace = null) : IGateExecutionProvider
{
    public const string EnableHint =
        "Windows Sandbox needs Windows 10/11 Pro, Enterprise or Education (24H2 or later for the `wsb` command line), " +
        "virtualization enabled in firmware, and the optional feature turned on once from an elevated PowerShell: " +
        "`Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All`, then reboot. " +
        "Alternatively set gate_execution.provider: host-unsafe to run gates on this machine without isolation.";

    // The runner inside enforces the command's own timeout; the host gives it this much longer before destroying the VM.
    private readonly TimeSpan _commandGrace = commandGrace ?? TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(30);
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(1);
    private readonly GateExecutionConfig _config = config;
    private readonly GateEnvStore _store = store;
    private readonly IReadOnlyList<Toolchain> _toolchains = toolchains;

    public string Name => GateExecutionConfig.WindowsSandboxProvider;

    public bool IsIsolated => true;

    public IReadOnlyList<Toolchain> Toolchains => _toolchains;

    public async Task<ProviderStatus> CheckAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ProviderStatus.Unavailable("Windows Sandbox exists only on Windows.");
        }

        try
        {
            var list = await Wsb(["list"], TimeSpan.FromSeconds(60), ct);
            if (!list.Succeeded)
            {
                return ProviderStatus.Unavailable($"`{wsbCommand} list` failed (exit {list.ExitCode}): {Short(list)}. {EnableHint}");
            }

            var running = SandboxIds(list.Stdout).Count;
            return ProviderStatus.Ok($"Windows Sandbox available ({running} running); {_toolchains.Count} toolchain folder(s) mapped read-only");
        }
        catch (ExecutableNotFoundException)
        {
            return ProviderStatus.Unavailable($"`{wsbCommand}` was not found. {EnableHint}");
        }
    }

    public async Task<IGateEnvironment> CreateAsync(GateEnvironmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var record = _store.Create(request.RunId, request.Label, Name);
        var env = new SandboxEnvironment(this, record, request);
        try
        {
            await env.StartAsync(ct);
            return env;
        }
        catch
        {
            await env.DestroyAsync(failed: true);
            throw;
        }
    }

    /// <summary>
    /// Stops sandboxes and deletes folders of environments whose owning Tokonomy process is gone (a crash, a killed
    /// terminal, a reboot). Environments kept on purpose (<c>keep-on-failure</c>) are only removed with <paramref name="includeKept"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecoverAsync(bool includeKept, CancellationToken ct)
    {
        var notes = new List<string>();
        foreach (var record in _store.List().Where(r => r.Provider is "" || r.Provider == Name))
        {
            var kept = File.Exists(Path.Combine(record.Directory, KeptMarker));
            if (!GateEnvStore.IsOrphaned(record) || (kept && !includeKept))
            {
                continue;
            }

            if (record.SandboxId is { } id && await StopAsync(id, ct) is { } problem)
            {
                notes.Add($"{record.EnvId}: {problem}");
                continue;   // never delete the folders of a sandbox that may still be running with them mapped
            }

            notes.Add(_store.Delete(record) is { } left ? $"{record.EnvId}: {left}" : $"{record.EnvId}: removed");
        }

        return notes;
    }

    internal const string KeptMarker = "KEPT";

    private Task<ProcessOutcome> Wsb(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        runner.RunAsync(new ProcessSpec
        {
            Args = [wsbCommand, .. args],
            WorkingDirectory = Environment.SystemDirectory,   // always exists (the store root may not yet)
            Timeout = timeout,
            MaxCaptureChars = 32 * 1024,
        }, ct);

    /// <summary>Exit code meaning "wsb did not report the command's exit code"; never success.</summary>
    internal const int UnknownExitCode = int.MinValue;

    /// <summary>
    /// Runs a command in the sandbox and returns an outcome whose <see cref="ProcessOutcome.ExitCode"/> is the
    /// <b>command's</b> exit code. Verified against wsb 0.8.107: <c>wsb exec</c> itself exits 0 whatever the command does,
    /// returns no stdout, and reports the command's code only in its output (<c>{"ExitCode": 7}</c> with <c>--raw</c>,
    /// <c>Process exited with code: 7</c> without). An output that carries no code counts as a failure.
    /// </summary>
    private async Task<ProcessOutcome> Exec(string id, string commandLine, TimeSpan timeout, CancellationToken ct)
    {
        var outcome = await Wsb(["exec", "--id", id, "-c", commandLine, "-r", "System", "--raw"], timeout, ct);
        if (outcome.TimedOut)
        {
            return outcome;
        }

        return outcome with { ExitCode = outcome.ExitCode == 0 ? ParseRemoteExitCode(outcome.Stdout) ?? UnknownExitCode : outcome.ExitCode };
    }

    internal static int? ParseRemoteExitCode(string output)
    {
        var match = RemoteExitRegex().Match(output);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    [GeneratedRegex(@"(?:""ExitCode""\s*:\s*|exited with code:\s*)(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteExitRegex();

    /// <summary>Stops a sandbox and confirms it is gone. Returns null on success, else the problem.</summary>
    private async Task<string?> StopAsync(string id, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var stop = await Wsb(["stop", "--id", id], TimeSpan.FromSeconds(120), ct);
            var list = await Wsb(["list"], TimeSpan.FromSeconds(60), ct);
            if (list.Succeeded && !SandboxIds(list.Stdout).Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            if (attempt == 3)
            {
                return $"sandbox {id} could not be stopped (exit {stop.ExitCode}: {Short(stop)}); stop it with `wsb stop --id {id}`";
            }

            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
        }

        return null;
    }

    internal static IReadOnlyList<string> SandboxIds(string text) =>
        GuidRegex().Matches(text).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string Short(ProcessOutcome o)
    {
        var text = (o.Stdout + " " + o.Stderr).Trim().ReplaceLineEndings(" ");
        return text.Length <= 300 ? text : text[..300] + "...";
    }

    /// <summary>The <c>.wsb</c> configuration: every device and channel off, every mapping read-only except the results folder.</summary>
    internal static string BuildConfiguration(IEnumerable<(string Host, string Sandbox, bool ReadOnly)> mappings, NetworkAccess network, int memoryMb)
    {
        var xml = new XElement("Configuration",
            new XElement("vGPU", "Disable"),
            new XElement("Networking", network == NetworkAccess.None ? "Disable" : "Default"),
            new XElement("MappedFolders", mappings.Select(m => new XElement("MappedFolder",
                new XElement("HostFolder", m.Host),
                new XElement("SandboxFolder", m.Sandbox),
                new XElement("ReadOnly", m.ReadOnly ? "true" : "false")))),
            new XElement("AudioInput", "Disable"),
            new XElement("VideoInput", "Disable"),
            new XElement("PrinterRedirection", "Disable"),
            new XElement("ClipboardRedirection", "Disable"),
            new XElement("ProtectedClient", "Enable"),
            new XElement("MemoryInMB", memoryMb.ToString(CultureInfo.InvariantCulture)));
        return xml.ToString(SaveOptions.DisableFormatting);
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();

    private sealed record Status(
        [property: System.Text.Json.Serialization.JsonPropertyName("nonce")] string? Nonce,
        [property: System.Text.Json.Serialization.JsonPropertyName("index")] int Index,
        [property: System.Text.Json.Serialization.JsonPropertyName("exit")] int Exit,
        [property: System.Text.Json.Serialization.JsonPropertyName("timed_out")] bool TimedOut,
        [property: System.Text.Json.Serialization.JsonPropertyName("not_found")] bool NotFound);

    private sealed class SandboxEnvironment(WindowsSandboxProvider owner, GateEnvRecord record, GateEnvironmentRequest request) : IGateEnvironment
    {
        private readonly string _src = Path.Combine(record.Directory, "src");
        private readonly string _ctl = Path.Combine(record.Directory, "ctl");
        private readonly string _out = Path.Combine(record.Directory, "out");
        private readonly string _nonce = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        private GateEnvRecord _record = record;
        private int _next;
        private bool _needsRestart;
        private bool _destroyed;

        public TimeSpan StartupTime { get; private set; }

        private GateArtifactLimits Limits => owner._config.ArtifactLimits;

        public async Task StartAsync(CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            Directory.CreateDirectory(_ctl);
            Directory.CreateDirectory(_out);
            SourceSnapshot.Copy(request.Source, _src, Limits.MaxSnapshotMb * 1024L * 1024);
            File.WriteAllText(Path.Combine(_ctl, SandboxRunnerScript.FileName), SandboxRunnerScript.Text);
            File.WriteAllText(Path.Combine(_ctl, "session.json"), JsonSerializer.Serialize(new
            {
                nonce = _nonce,
                toolchains = owner._toolchains.Select(t => t.SandboxPath).ToArray(),
            }));
            await BootAsync(ct);
            StartupTime = stopwatch.Elapsed;
        }

        private async Task BootAsync(CancellationToken ct)
        {
            // One sandbox at a time: a sandbox someone else started is reported, never stopped.
            var list = await owner.Wsb(["list"], TimeSpan.FromSeconds(60), ct);
            if (!list.Succeeded)
            {
                throw new GateEnvironmentException($"`wsb list` failed (exit {list.ExitCode}): {Short(list)}. {EnableHint}");
            }

            var running = SandboxIds(list.Stdout);
            if (running.Count > 0)
            {
                throw new GateEnvironmentException(
                    $"another Windows Sandbox is running ({string.Join(", ", running)}). Windows Sandbox runs one instance at a time; " +
                    "close it and run again (Tokonomy never stops a sandbox it did not start; `tokonomy doctor` removes ones left by crashed runs).");
            }

            var mappings = new List<(string, string, bool)>
            {
                (_src, SandboxRunnerScript.SandboxSource, true),
                (_ctl, SandboxRunnerScript.SandboxControl, true),
                (_out, SandboxRunnerScript.SandboxOutput, false),
            };
            if (request.Dependencies is { } deps)
            {
                mappings.Add((Path.GetDirectoryName(Path.GetFullPath(deps))!, SandboxRunnerScript.SandboxDependencies, true));
            }

            mappings.AddRange(owner._toolchains.Select(t => (t.HostPath, t.SandboxPath, true)));
            var xml = BuildConfiguration(mappings, request.Network, owner._config.ResourceLimits.MemoryMb);

            var start = await owner.Wsb(["start", "--config", xml], TimeSpan.FromSeconds(owner._config.StartupTimeoutSeconds), ct);
            var ids = SandboxIds(start.Stdout + "\n" + start.Stderr);
            var id = ids.Count > 0 ? ids[0] : null;
            if (id is not null)
            {
                _record = _record with { SandboxId = id };
                owner._store.Save(_record);
            }

            if (!start.Succeeded || id is null)
            {
                throw new GateEnvironmentException($"Windows Sandbox did not start (exit {start.ExitCode}): {Short(start)}");
            }

            var deadline = DateTime.UtcNow.AddSeconds(owner._config.StartupTimeoutSeconds);
            while (true)
            {
                var probe = await owner.Exec(id, SandboxRunnerScript.CommandLine("probe"), TimeSpan.FromSeconds(60), ct);
                if (probe.Succeeded)
                {
                    break;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new GateEnvironmentException($"Windows Sandbox {id} did not accept commands within {owner._config.StartupTimeoutSeconds}s: {Short(probe)}");
                }

                await Task.Delay(owner._poll, ct);
            }

            var setup = await owner.Exec(id, SandboxRunnerScript.CommandLine("setup"), TimeSpan.FromSeconds(owner._config.StartupTimeoutSeconds) + TimeSpan.FromMinutes(10), ct);
            if (!setup.Succeeded)
            {
                throw new GateEnvironmentException($"copying the source inside the sandbox failed (exit {setup.ExitCode}): {ReadOut("setup.log") ?? ReadOut("runner-error.log") ?? Short(setup)}");
            }

            _needsRestart = false;
        }

        public async Task<GateCommandResult> RunAsync(GateCommand command, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(command);
            ObjectDisposedException.ThrowIf(_destroyed, this);
            if (_needsRestart)
            {
                // A command overran its timeout and the sandbox was destroyed with everything in it; continue in a fresh one.
                await StopCurrentAsync(ct);
                await BootAsync(ct);
            }

            var index = _next++;
            // The spec travels inside the exec command line, not as a file: files the host adds to a mapped folder after
            // boot can be invisible inside the sandbox for a moment (seen live), and the control folder stays as booted.
            var spec = SandboxRunnerScript.EncodeSpec(command, Limits);
            var stopwatch = Stopwatch.StartNew();
            ProcessOutcome exec;
            try
            {
                exec = await owner.Exec(_record.SandboxId!, SandboxRunnerScript.CommandLine("run", index, spec), command.Timeout + owner._commandGrace, ct);
            }
            catch (OperationCanceledException)
            {
                await StopCurrentAsync(CancellationToken.None);   // cancellation ends everything the command started
                _needsRestart = true;
                throw;
            }

            if (exec.TimedOut)
            {
                await StopCurrentAsync(ct);
                _needsRestart = true;
                return new GateCommandResult
                {
                    Termination = GateTermination.TimedOut,
                    Output = $"[tokonomy] '{command.Name}' did not finish within {command.Timeout.TotalSeconds:0}s; the sandbox was destroyed with everything running in it",
                    Duration = stopwatch.Elapsed,
                };
            }

            RejectLinksInOutput();
            var output = ReadOut($"{index}.log", Limits.MaxLogKb * 1024L + 64 * 1024) ?? "";
            if (command.LogFile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(command.LogFile))!);
                File.WriteAllText(command.LogFile, output);
            }

            string? report = null;
            if (command.ReportPath is not null && command.ReportDestination is not null)
            {
                if (File.Exists(command.ReportDestination))
                {
                    File.Delete(command.ReportDestination);
                }

                var copied = Guarded(() => WorktreeGuard.CopyFileOut(_out, $"{index}.report", command.ReportDestination, Limits.MaxReportMb * 1024L * 1024));
                if (copied < 0)
                {
                    output += $"\n[tokonomy] the test report exceeds {Limits.MaxReportMb} MB and was ignored";
                }

                report = copied > 0 ? command.ReportDestination : null;
            }

            var status = ReadStatus(index);
            var termination = status switch
            {
                null => GateTermination.ProtocolViolation,
                { NotFound: true } => GateTermination.NotFound,
                { TimedOut: true } => GateTermination.TimedOut,
                _ => GateTermination.Completed,
            };

            // Both channels come from inside the sandbox; any disagreement is a failure, never a pass.
            var exit = status?.Exit;
            if (termination == GateTermination.Completed && exit == 0 && exec.ExitCode != 0)
            {
                termination = GateTermination.ProtocolViolation;
                output += $"\n[tokonomy] the command reported success but the sandbox returned exit code {exec.ExitCode}; treated as a failure";
            }

            if (status is null)
            {
                output += $"\n[tokonomy] no valid status came back from the sandbox (exit {exec.ExitCode}); treated as a failure. {ReadOut("runner-error.log") ?? Short(exec)}";
            }

            return new GateCommandResult
            {
                Termination = termination,
                ExitCode = exit,
                Output = output.Trim(),
                ReportFile = report,
                Duration = stopwatch.Elapsed,
            };
        }

        public async Task<string?> ExportDependenciesAsync(string destination, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            var exec = await owner.Exec(_record.SandboxId!, SandboxRunnerScript.CommandLine("export"), ExportTimeout, ct);
            RejectLinksInOutput();
            if (!exec.Succeeded)
            {
                throw new GateEnvironmentException($"archiving prepared dependencies failed (exit {exec.ExitCode}): {ReadOut("export.log") ?? ReadOut("runner-error.log") ?? Short(exec)}");
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            var copied = Guarded(() => WorktreeGuard.CopyFileOut(_out, SandboxRunnerScript.DependencyArchive, destination, Limits.MaxDependenciesMb * 1024L * 1024));
            return copied switch
            {
                null => throw new GateEnvironmentException("prepare produced no dependency archive"),
                < 0 => throw new GateSecurityException($"the dependency archive exceeds gate_execution.artifact_limits.max_dependencies_mb ({Limits.MaxDependenciesMb} MB)"),
                _ => destination,
            };
        }

        public async Task<string?> DestroyAsync(bool failed = false)
        {
            if (_destroyed)
            {
                return null;
            }

            _destroyed = true;
            var problem = await StopCurrentAsync(CancellationToken.None);
            if (problem is not null)
            {
                return problem;   // folders stay mapped into a sandbox that may still run; recovery retries later
            }

            if (failed && owner._config.CleanupPolicy == GateExecutionConfig.CleanupKeepOnFailure)
            {
                File.WriteAllText(Path.Combine(_record.Directory, KeptMarker), "kept for inspection (gate_execution.cleanup_policy: keep-on-failure)");
                return $"kept {_record.Directory} for inspection (keep-on-failure)";
            }

            return owner._store.Delete(_record);
        }

        public async ValueTask DisposeAsync() => await DestroyAsync();

        private async Task<string?> StopCurrentAsync(CancellationToken ct)
        {
            if (_record.SandboxId is not { } id)
            {
                return null;
            }

            var problem = await owner.StopAsync(id, ct);
            if (problem is null)
            {
                _record = _record with { SandboxId = null };
                if (Directory.Exists(_record.Directory))   // recovery (e.g. `doctor`) may already have removed it
                {
                    owner._store.Save(_record);
                }
            }

            return problem;
        }

        /// <summary>
        /// A link in the results folder can only have been made by code inside the sandbox, and following one on the host
        /// could read or overwrite any file the user can reach. It is removed (as a link) and the batch fails.
        /// </summary>
        private void RejectLinksInOutput()
        {
            var links = WorktreeGuard.FindLinks(_out);
            if (links.Count == 0)
            {
                return;
            }

            WorktreeGuard.Unlink(links);
            throw new GateSecurityException(
                $"the gate environment created link(s) in its results folder ({string.Join(", ", links.Take(3).Select(l => $"{l.RelativePath} -> {l.Target}"))}); nothing from it is used");
        }

        private string? ReadOut(string name, long maxBytes = 64 * 1024) =>
            Guarded(() => WorktreeGuard.ReadAllText(_out, name, maxBytes));

        private Status? ReadStatus(int index)
        {
            try
            {
                var text = ReadOut($"{index}.status.json");
                var status = text is null ? null : JsonSerializer.Deserialize<Status>(text);
                return status is not null && status.Nonce == _nonce && status.Index == index ? status : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static T Guarded<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (WorktreeEscapeException ex)
            {
                throw new GateSecurityException($"unsafe file in the gate environment's results: {ex.Message}");
            }
        }
    }
}
