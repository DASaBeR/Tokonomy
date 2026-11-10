using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tokonomy.Runtime.Process;

/// <summary>
/// Knows every subprocess Tokonomy started, so Ctrl+C, a forced exit, a closed terminal or a crash cannot leave one
/// running. On Windows each child (and everything it starts) is put in a job object that Windows ends together with
/// this process; <see cref="KillAll"/> does the same by hand on every platform.
/// </summary>
public static class ChildProcesses
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private static readonly ConcurrentDictionary<int, System.Diagnostics.Process> Live = new();
    private static readonly Lazy<nint> Job = new(CreateKillOnCloseJob);
    private static int _busy;

    /// <summary>Number of long operations in flight (a run, a subprocess). Zero means nothing needs a graceful stop.</summary>
    public static int Busy => Volatile.Read(ref _busy);

    public static int Count => Live.Count;

    /// <summary>Marks a stretch of work (including its cleanup) as one a Ctrl+C should wait for.</summary>
    public static IDisposable BeginWork()
    {
        Interlocked.Increment(ref _busy);
        return new Registration(() => Interlocked.Decrement(ref _busy));
    }

    /// <summary>Tracks a started process and ties its lifetime to this process. Dispose when the process is done.</summary>
    internal static IDisposable Adopt(System.Diagnostics.Process process)
    {
        var id = process.Id;
        Live[id] = process;
        Interlocked.Increment(ref _busy);
        AssignToJob(process);
        return new Registration(() =>
        {
            Live.TryRemove(id, out _);
            Interlocked.Decrement(ref _busy);
        });
    }

    /// <summary>Kills every tracked process and everything it started. Safe to call at any time.</summary>
    public static void KillAll()
    {
        foreach (var process in Live.Values)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Already gone.
            }
        }
    }

    /// <summary>True when the process is inside Tokonomy's kill-on-close job (always false off Windows).</summary>
    internal static bool IsInJob(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsWindows() || Job.Value == 0)
        {
            return false;
        }

        try
        {
            return IsProcessInJob(process.Handle, Job.Value, out var result) && result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static void AssignToJob(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsWindows() || Job.Value == 0)
        {
            return;
        }

        try
        {
            // Best effort: the child may already have exited, or a parent job may forbid it. KillAll still covers those.
            AssignProcessToJobObject(Job.Value, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static nint CreateKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var job = CreateJobObject(0, null);
        if (job == 0)
        {
            return 0;
        }

        var info = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                CloseHandle(job);
                return 0;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // The handle is deliberately never closed: Windows closes it when this process ends, however it ends,
        // and that ends every process still inside the job.
        return job;
    }

    private sealed class Registration(Action onDispose) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                onDispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint size);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
