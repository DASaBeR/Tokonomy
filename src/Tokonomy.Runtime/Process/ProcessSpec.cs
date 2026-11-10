namespace Tokonomy.Runtime.Process;

/// <summary>A subprocess invocation. Arguments are always an array; nothing is parsed by a shell.</summary>
public sealed record ProcessSpec
{
    public required IReadOnlyList<string> Args { get; init; }
    public required string WorkingDirectory { get; init; }

    /// <summary>Variables to set. With <see cref="ReplaceEnvironment"/> these become the entire environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>When true the child does not inherit the parent environment (used for worker scrubbing).</summary>
    public bool ReplaceEnvironment { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// The proxy settings for this process (already narrowed to its connection, see <c>ProxySettings.ForProvider</c>).
    /// Unset: the user's proxy as it applies to "other" tools such as git and the gates.
    /// </summary>
    public Tokonomy.Core.Net.ProxySettings? Proxy { get; init; }

    /// <summary>Text written to the child's stdin (UTF-8) before it is closed. Null closes stdin immediately.</summary>
    public string? StdinText { get; init; }

    /// <summary>If set, full stdout is also written here (capped at <see cref="MaxFileBytes"/>).</summary>
    public string? StdoutFile { get; init; }

    public string? StderrFile { get; init; }

    /// <summary>In-memory capture keeps the last N characters of each stream (the tail holds most failure summaries).</summary>
    public int MaxCaptureChars { get; init; } = 256 * 1024;

    public long MaxFileBytes { get; init; } = 50L * 1024 * 1024;
}

public sealed record ProcessOutcome
{
    public required int ExitCode { get; init; }
    public required bool TimedOut { get; init; }
    public required TimeSpan Duration { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public bool Truncated { get; init; }

    public bool Succeeded => !TimedOut && ExitCode == 0;
}

public sealed class ExecutableNotFoundException(string command)
    : Exception($"Executable '{command}' was not found on PATH.")
{
    public string Command { get; } = command;
}
