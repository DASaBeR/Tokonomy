using System.Diagnostics;
using System.Text;
using Tokonomy.Core.Net;

namespace Tokonomy.Runtime.Process;

public interface IProcessRunner
{
    Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct);
}

/// <summary>
/// Runs subprocesses with argument arrays, a timeout, full process-tree kill and bounded output capture.
/// Stdin is closed immediately so agents that prompt for input fail fast instead of hanging.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    public async Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Args.Count == 0)
        {
            throw new ArgumentException("Process spec has no command.", nameof(spec));
        }

        var psi = BuildStartInfo(spec);
        using var process = new System.Diagnostics.Process { StartInfo = psi };

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        using var tracked = ChildProcesses.Adopt(process);
        if (spec.StdinText is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(spec.StdinText.AsMemory(), ct);
            }
            catch (IOException)
            {
                // The child exited before reading everything; its exit code and output tell the story.
            }
        }

        process.StandardInput.Close();

        var stdout = new StreamCapture(spec.MaxCaptureChars, spec.StdoutFile, spec.MaxFileBytes);
        var stderr = new StreamCapture(spec.MaxCaptureChars, spec.StderrFile, spec.MaxFileBytes);
        var stdoutTask = stdout.PumpAsync(process.StandardOutput);
        var stderrTask = stderr.PumpAsync(process.StandardError);

        using var timeoutCts = spec.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested)
            {
                await DrainAsync(stdoutTask, stderrTask);
                stdout.Dispose();
                stderr.Dispose();
                throw;
            }

            timedOut = true;
        }

        await DrainAsync(stdoutTask, stderrTask);
        stopwatch.Stop();
        stdout.Dispose();
        stderr.Dispose();

        return new ProcessOutcome
        {
            ExitCode = timedOut ? -1 : process.ExitCode,
            TimedOut = timedOut,
            Duration = stopwatch.Elapsed,
            Stdout = stdout.Text,
            Stderr = stderr.Text,
            Truncated = stdout.Truncated || stderr.Truncated,
        };
    }

    internal static ProcessStartInfo BuildStartInfo(ProcessSpec spec, ProxySettings? proxy = null)
    {
        var resolved = ExecutableResolver.Resolve(spec.Args[0], spec.WorkingDirectory, spec.Environment);
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (ExecutableResolver.NeedsCmdWrapper(resolved))
        {
            psi.FileName = System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = ExecutableResolver.BuildCmdArguments(resolved, spec.Args.Skip(1));
        }
        else
        {
            psi.FileName = resolved;
            foreach (var arg in spec.Args.Skip(1))
            {
                psi.ArgumentList.Add(arg);
            }
        }

        if (spec.ReplaceEnvironment)
        {
            psi.Environment.Clear();
        }

        if (spec.Environment is not null)
        {
            foreach (var (key, value) in spec.Environment)
            {
                psi.Environment[key] = value;
            }
        }

        // The user's proxy applies to every process Tokonomy starts, even one whose environment was not built by
        // EnvironmentBuilder (version checks, sign-in status, anything added later). Variables the spec sets itself win.
        var comparer = System.OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var effective = spec.Proxy ?? (proxy ?? ProxySettings.Current).ForProvider(ProxySettings.OtherId);
        foreach (var (key, value) in effective.ChildEnvironment())
        {
            if (spec.Environment is null || !spec.Environment.Keys.Contains(key, comparer))
            {
                psi.Environment[key] = value;
            }
        }

        return psi;
    }

    private static void KillTree(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static async Task DrainAsync(Task a, Task b)
    {
        // A surviving grandchild could keep a pipe open; never block forever on it.
        await Task.WhenAny(Task.WhenAll(a, b), Task.Delay(DrainGrace));
    }

    private sealed class StreamCapture(int maxChars, string? filePath, long maxFileBytes) : IDisposable
    {
        private readonly StringBuilder _buffer = new();
        private readonly Lock _gate = new();
        private StreamWriter? _file;
        private long _fileBytes;
        private bool _disposed;

        public bool Truncated { get; private set; }

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _buffer.ToString();
                }
            }
        }

        public async Task PumpAsync(StreamReader reader)
        {
            if (filePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
                _file = new StreamWriter(filePath, append: false, new UTF8Encoding(false));
            }

            var chunk = new char[8192];
            int read;
            while ((read = await reader.ReadAsync(chunk.AsMemory())) > 0)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _buffer.Append(chunk, 0, read);
                    if (_buffer.Length > maxChars * 2)
                    {
                        _buffer.Remove(0, _buffer.Length - maxChars);
                        Truncated = true;
                    }

                    if (_file is not null && _fileBytes < maxFileBytes)
                    {
                        _file.Write(chunk, 0, read);
                        _fileBytes += read;
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_buffer.Length > maxChars)
                {
                    _buffer.Remove(0, _buffer.Length - maxChars);
                    Truncated = true;
                }

                _file?.Dispose();
            }
        }
    }
}
