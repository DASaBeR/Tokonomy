namespace Tokonomy.Runtime.Gates.Isolation;

/// <summary>
/// Runs gate and prepare commands somewhere code from the worktree cannot reach the host. The orchestrator knows only
/// this interface; Windows Sandbox and the explicit host fallback are implementations.
/// </summary>
public interface IGateExecutionProvider
{
    /// <summary>The <c>gate_execution.provider</c> value.</summary>
    string Name { get; }

    /// <summary>False only for <c>host-unsafe</c>: commands run with the user's privileges.</summary>
    bool IsIsolated { get; }

    /// <summary>Whether this provider can run here, with a diagnostic when it cannot. Cheap; never starts an environment.</summary>
    Task<ProviderStatus> CheckAsync(CancellationToken ct);

    /// <summary>
    /// Creates one environment holding a private copy of <see cref="GateEnvironmentRequest.Source"/>. Throws
    /// <see cref="GateSecurityException"/> when the source contains links or other objects that must not be copied and
    /// <see cref="GateEnvironmentException"/> when the environment cannot be provisioned.
    /// </summary>
    Task<IGateEnvironment> CreateAsync(GateEnvironmentRequest request, CancellationToken ct);
}

/// <summary>
/// One disposable environment. Commands run one after another and share its working copy (like gates sharing a
/// worktree). Disposing destroys it; <see cref="DestroyAsync"/> does the same and reports what could not be cleaned up.
/// </summary>
public interface IGateEnvironment : IAsyncDisposable
{
    /// <summary>Wall time spent creating the environment (boot, copy), for the ledger and measurements.</summary>
    TimeSpan StartupTime { get; }

    /// <summary>
    /// Runs one command. Timeouts are results (<see cref="GateTermination.TimedOut"/>), cancellation throws
    /// <see cref="OperationCanceledException"/> after everything the command started has been terminated.
    /// </summary>
    Task<GateCommandResult> RunAsync(GateCommand command, CancellationToken ct);

    /// <summary>
    /// Exports what prepare steps produced (files that are not part of the source, plus package caches) as one opaque
    /// archive at <paramref name="destination"/>. The host never unpacks it; later environments receive it read-only.
    /// Returns null when the provider has no such step (host-unsafe prepares in place).
    /// </summary>
    Task<string?> ExportDependenciesAsync(string destination, CancellationToken ct);

    /// <summary>Destroys the environment. Idempotent. Returns null when everything was cleaned up, else what was left.</summary>
    Task<string?> DestroyAsync(bool failed = false);
}

public sealed record ProviderStatus(bool Available, string Detail)
{
    public static ProviderStatus Ok(string detail) => new(true, detail);

    public static ProviderStatus Unavailable(string detail) => new(false, detail);
}

public enum NetworkAccess
{
    None,
    Full,
}

/// <summary>
/// Files to copy into the environment: paths relative to <see cref="Root"/>, as git lists them (tracked plus new
/// non-ignored files). Never includes <c>.git</c> or <c>.tokonomy</c>.
/// </summary>
public sealed record GateSource(string Root, IReadOnlyList<string> Files);

public sealed record GateEnvironmentRequest
{
    /// <summary>Run that owns the environment; recorded so a crashed run's environments can be found and destroyed.</summary>
    public required string RunId { get; init; }

    /// <summary>Short label for folders and diagnostics (<c>baseline</c>, <c>u1-a2</c>, <c>prepare</c>).</summary>
    public required string Label { get; init; }

    public required GateSource Source { get; init; }
    public NetworkAccess Network { get; init; } = NetworkAccess.None;

    /// <summary>Archive from <see cref="IGateEnvironment.ExportDependenciesAsync"/>, unpacked inside the environment before the source.</summary>
    public string? Dependencies { get; init; }
}

/// <summary>One command. <see cref="ReportPath"/> is relative to the working copy.</summary>
public sealed record GateCommand
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public required TimeSpan Timeout { get; init; }
    public string? ReportPath { get; init; }

    /// <summary>Host file the (capped) combined output is written to.</summary>
    public string? LogFile { get; init; }

    /// <summary>Host file a collected report is copied to; required when <see cref="ReportPath"/> is set.</summary>
    public string? ReportDestination { get; init; }
}

public enum GateTermination
{
    Completed,
    TimedOut,

    /// <summary>The executable does not exist in the environment.</summary>
    NotFound,

    /// <summary>The result could not be trusted (missing or inconsistent status from the environment). Counts as a failure.</summary>
    ProtocolViolation,
}

public sealed record GateCommandResult
{
    public required GateTermination Termination { get; init; }
    public int? ExitCode { get; init; }
    public required string Output { get; init; }

    /// <summary>Validated copy of the command's report on the host, or null.</summary>
    public string? ReportFile { get; init; }

    public TimeSpan Duration { get; init; }

    public bool Succeeded => Termination == GateTermination.Completed && ExitCode == 0;
}

/// <summary>The configured provider cannot run on this machine. The run stops before any worker starts.</summary>
public sealed class GateIsolationUnavailableException(string message) : Exception(message);

/// <summary>Provisioning, transfer or teardown failed. Infrastructure, not the worker's fault: the run stops with an error.</summary>
public sealed class GateEnvironmentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The source or the environment's output contained something that must never cross the boundary (a link, a hard link,
/// a path that escapes, an oversized file). Treated like a workspace violation: the unit fails and is not retried.
/// </summary>
public sealed class GateSecurityException(string message) : Exception(message);
