using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Tokonomy.Runtime.Git;

/// <summary>A refused file operation: the path, or something on the way to it, leaves the worktree.</summary>
public sealed class WorktreeEscapeException(string message) : IOException(message);

/// <summary>A symbolic link, junction or other reparse point found inside a worktree.</summary>
public sealed record WorktreeLink(string RelativePath, string FullPath, bool IsDirectory, string? Target, bool Escapes);

/// <summary>
/// Every read, write and delete Tokonomy itself performs inside a worktree goes through here, because a worker (or code
/// a gate runs) can plant links and the obvious <c>File.ReadAllText(Path.Combine(root, rel))</c> follows them.
/// <list type="bullet">
/// <item>Relative paths are validated lexically: no rooted paths, no <c>..</c>, no alternate data streams, and on Windows
/// no trailing dots or spaces and no device names (Win32 silently rewrites those, e.g. <c>.tokonomy.</c> is <c>.tokonomy</c>).</item>
/// <item>No component between the root and the target may be a reparse point (symlink, junction, mount point).</item>
/// <item>On Windows the target is opened with <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, so a link is opened as itself and never
/// followed; the opened handle is then checked again (not a reparse point, one hard link, final path inside the root).
/// Checking the handle we actually use closes the gap between "check" and "use" for the final component.</item>
/// </list>
/// On Linux and macOS the component checks run immediately before the operation, which leaves a small race window that
/// only a process running concurrently in the worktree could use; the threat model documents it.
/// </summary>
public static partial class WorktreeGuard
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Validates <paramref name="relative"/> and returns its full path under <paramref name="root"/>.</summary>
    public static string ResolveInside(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(relative);
        var normalized = relative.Replace('\\', '/');
        if (normalized.Length == 0 || Path.IsPathRooted(relative) || normalized.StartsWith('/'))
        {
            throw new WorktreeEscapeException($"'{relative}' is not a relative path inside the worktree");
        }

        if (relative.Contains(':', StringComparison.Ordinal) || relative.Contains('\0', StringComparison.Ordinal))
        {
            throw new WorktreeEscapeException($"'{relative}' contains ':' (drive or alternate data stream)");
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                throw new WorktreeEscapeException($"'{relative}' contains an empty, '.' or '..' segment");
            }

            if (OperatingSystem.IsWindows())
            {
                if (segment.EndsWith('.') || segment.EndsWith(' '))
                {
                    throw new WorktreeEscapeException($"'{relative}': a segment ending in '.' or ' ' is rewritten by Windows");
                }

                if (DeviceName().IsMatch(segment))
                {
                    throw new WorktreeEscapeException($"'{relative}': '{segment}' is a reserved device name on Windows");
                }
            }
        }

        var full = Path.GetFullPath(Path.Combine(RootOf(root), normalized));
        if (!IsInside(root, full))
        {
            throw new WorktreeEscapeException($"'{relative}' resolves outside the worktree");
        }

        return full;
    }

    /// <summary>Lexical containment on normalized full paths (case-insensitive on Windows); the root itself is not "inside".</summary>
    public static bool IsInside(string root, string path)
    {
        var r = RootOf(root) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, PathComparison);
    }

    /// <summary>Reads a file, or returns null when it does not exist. Throws <see cref="WorktreeEscapeException"/> on a link.</summary>
    public static string? ReadAllText(string root, string relative, long maxBytes = long.MaxValue)
    {
        var full = ResolveInside(root, relative);
        EnsureNoLinkOnTheWay(root, full, includeTarget: false);
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenNoFollow(full, write: false);
            if (handle is null)
            {
                return null;
            }

            CheckHandle(root, full, handle, created: false);
            using var stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length > maxBytes)
            {
                return null;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return IsLink(full) ? throw Escape(full) : null;
        }

        EnsureNoLinkOnTheWay(root, full, includeTarget: true);
        return new FileInfo(full).Length > maxBytes ? null : File.ReadAllText(full);
    }

    /// <summary>
    /// Copies a regular file from inside <paramref name="root"/> to <paramref name="destination"/> (which must not exist)
    /// with the same rules as <see cref="ReadAllText"/>: no link on the way, the file opened as itself, one hard link,
    /// final path inside the root. Returns the number of bytes copied, null when the file does not exist, or -1 when it is
    /// larger than <paramref name="maxBytes"/> (nothing is written then). Throws <see cref="WorktreeEscapeException"/> on a link.
    /// </summary>
    public static long? CopyFileOut(string root, string relative, string destination, long maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var full = ResolveInside(root, relative);
        EnsureNoLinkOnTheWay(root, full, includeTarget: false);
        FileStream source;
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenNoFollow(full, write: false);
            if (handle is null)
            {
                return null;
            }

            try
            {
                CheckHandle(root, full, handle, created: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            source = new FileStream(handle, FileAccess.Read);
        }
        else
        {
            if (!File.Exists(full))
            {
                return IsLink(full) ? throw Escape(full) : null;
            }

            EnsureNoLinkOnTheWay(root, full, includeTarget: true);
            source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        using (source)
        {
            if (source.Length > maxBytes)
            {
                return -1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    target.Dispose();
                    File.Delete(destination);
                    return -1;   // grew while we were reading
                }

                target.Write(buffer, 0, read);
            }

            return total;
        }
    }

    /// <summary>
    /// Deletes a directory tree Tokonomy owns without following anything in it: links are removed as links first, and
    /// when one cannot be removed the tree is left in place. Returns null on success, else why it was left.
    /// </summary>
    public static string? DeleteTree(string path)
    {
        var full = RootOf(path);
        if (!Directory.Exists(full) && !IsLink(full))
        {
            return null;
        }

        if (IsLink(full))
        {
            return $"{full} is itself a link; not deleting through it";
        }

        var stuck = Unlink(FindLinks(full));
        if (stuck.Count > 0 || FindLinks(full).Count > 0)
        {
            return $"{full} still contains links that could not be removed; left in place";
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);   // read-only files would block the delete
                }

                Directory.Delete(full, recursive: true);
                return null;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 4)
            {
                Thread.Sleep(250 * attempt);   // AV scanners and exiting processes hold handles briefly
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"could not delete {full}: {ex.Message}";
            }
        }
    }

    /// <summary>Writes (creating or truncating) a file inside the worktree, creating missing directories one by one.</summary>
    public static void WriteAllText(string root, string relative, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = ResolveInside(root, relative);
        CreateParents(root, full);
        var bytes = new UTF8Encoding(false).GetBytes(content);
        if (OperatingSystem.IsWindows())
        {
            var existed = File.Exists(full) || Directory.Exists(full) || IsLink(full);
            using var handle = OpenNoFollow(full, write: true) ?? throw new IOException($"could not open {full}");
            CheckHandle(root, full, handle, created: !existed);
            using var stream = new FileStream(handle, FileAccess.ReadWrite);
            stream.SetLength(0);
            stream.Write(bytes);
            return;
        }

        EnsureNoLinkOnTheWay(root, full, includeTarget: true);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>Deletes a file inside the worktree. A link at that path is removed as a link; its target is never touched.</summary>
    public static void DeleteFile(string root, string relative)
    {
        var full = ResolveInside(root, relative);
        EnsureNoLinkOnTheWay(root, full, includeTarget: false);
        if (IsLink(full))
        {
            Unlink(full);
        }
        else if (File.Exists(full))
        {
            File.Delete(full);
        }
    }

    /// <summary>
    /// Every symlink, junction and other reparse point under <paramref name="root"/>, without ever descending into one.
    /// <c>.git</c> at the root is skipped (git's own data). A link "escapes" when its final target is outside the root,
    /// cannot be resolved, or is a reparse point of a kind .NET cannot read (treated as unsafe).
    /// </summary>
    public static IReadOnlyList<WorktreeLink> FindLinks(string root)
    {
        var rootFull = RootOf(root);
        var links = new List<WorktreeLink>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(rootFull));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (string.Equals(entry.FullName, Path.Combine(rootFull, ".git"), PathComparison) && !IsLink(entry.FullName))
                {
                    continue;
                }

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                {
                    links.Add(Describe(rootFull, entry));
                }
                else if (entry is DirectoryInfo sub)
                {
                    pending.Push(sub);
                }
            }
        }

        return links.OrderBy(l => l.RelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>Removes the links themselves (never their targets). Returns the ones that could not be removed.</summary>
    public static IReadOnlyList<WorktreeLink> Unlink(IEnumerable<WorktreeLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        var failed = new List<WorktreeLink>();
        foreach (var link in links)
        {
            try
            {
                Unlink(link.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(link);
            }
        }

        return failed;
    }

    private static void Unlink(string path)
    {
        // Only symlinks and junctions are removed: a reparse point .NET cannot describe (e.g. a cloud-files placeholder)
        // is real data, not a link, and deleting it would delete the file.
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is null)
        {
            throw new IOException($"{path} is a reparse point that is not a symlink or junction; not removing it");
        }

        if (info is DirectoryInfo)
        {
            // Non-recursive: RemoveDirectory on a junction or directory symlink removes the link only.
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static WorktreeLink Describe(string rootFull, FileSystemInfo entry)
    {
        var relative = Path.GetRelativePath(rootFull, entry.FullName).Replace('\\', '/');
        string? target = null;
        var escapes = true;
        try
        {
            target = entry.LinkTarget;
            if (target is not null)
            {
                var final = entry.ResolveLinkTarget(returnFinalTarget: true);
                var resolved = final?.FullName ?? Path.GetFullPath(target, Path.GetDirectoryName(entry.FullName)!);
                escapes = !IsInside(rootFull, resolved) && !string.Equals(RootOf(resolved), rootFull, PathComparison);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            escapes = true;
        }

        return new WorktreeLink(relative, entry.FullName, entry is DirectoryInfo, target, escapes);
    }

    private static void CreateParents(string root, string full)
    {
        var rootFull = RootOf(root);
        var parent = Path.GetDirectoryName(full)!;
        var missing = new Stack<string>();
        for (var p = parent; !string.Equals(p, rootFull, PathComparison); p = Path.GetDirectoryName(p)!)
        {
            if (!Directory.Exists(p) && !File.Exists(p) && !IsLink(p))
            {
                missing.Push(p);
            }
        }

        EnsureNoLinkOnTheWay(root, parent, includeTarget: true);
        while (missing.Count > 0)
        {
            var dir = missing.Pop();
            Directory.CreateDirectory(dir);
            if (IsLink(dir))
            {
                throw Escape(dir);
            }
        }
    }

    /// <summary>Throws if the root or any directory between the root and <paramref name="full"/> is a link.</summary>
    private static void EnsureNoLinkOnTheWay(string root, string full, bool includeTarget)
    {
        var rootFull = RootOf(root);
        if (IsLink(rootFull))
        {
            throw Escape(rootFull);
        }

        var start = includeTarget ? full : Path.GetDirectoryName(full)!;
        for (var p = start; p.Length > rootFull.Length && !string.Equals(p, rootFull, PathComparison); p = Path.GetDirectoryName(p)!)
        {
            if (IsLink(p))
            {
                throw Escape(p);
            }
        }
    }

    /// <summary>True for a symlink, junction or any reparse point. Reads the entry itself; never follows it.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static WorktreeEscapeException Escape(string path) =>
        new($"refusing to follow a link: {path} is a symlink, junction or reparse point");

    private static string RootOf(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$)(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceName();

    // ---- Windows: open without following links and verify the handle --------------------------------------------

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileAttributeDirectory = 0x10;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    private static SafeFileHandle? OpenNoFollow(string full, bool write)
    {
        var handle = CreateFileW(
            full,
            write ? GenericRead | GenericWrite : GenericRead,
            write ? 0 : FileShareRead,
            IntPtr.Zero,
            write ? OpenAlways : OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return !write && error is ErrorFileNotFound or ErrorPathNotFound
            ? null
            : throw new IOException($"could not open {full} (Win32 error {error})");
    }

    private static void CheckHandle(string root, string full, SafeFileHandle handle, bool created)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new IOException($"could not inspect {full} (Win32 error {Marshal.GetLastPInvokeError()})");
        }

        if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw Escape(full);
        }

        if ((info.FileAttributes & FileAttributeDirectory) != 0)
        {
            throw new IOException($"{full} is a directory");
        }

        if (info.NumberOfLinks > 1)
        {
            throw new WorktreeEscapeException($"refusing to use {full}: it is hard-linked ({info.NumberOfLinks} names), so it may be a file outside the worktree");
        }

        // A directory on the way could have been swapped for a junction after it was checked. The handle tells the truth.
        var final = FinalPath(handle);
        var rootFinal = FinalPathOfDirectory(RootOf(root));
        if (final is null || rootFinal is null || !final.StartsWith(rootFinal + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            if (created && final is not null)
            {
                handle.Dispose();
                File.Delete(final);   // we created it a moment ago; take it back
            }

            throw new WorktreeEscapeException($"refusing to use {full}: it resolves to {final ?? "an unknown location"}, outside the worktree");
        }
    }

    private static string? FinalPathOfDirectory(string directory)
    {
        using var handle = CreateFileW(directory, 0, 0x7, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        return handle.IsInvalid ? null : FinalPath(handle);
    }

    private static unsafe string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint length;
        fixed (char* p = buffer)
        {
            length = GetFinalPathNameByHandleW(handle, p, (uint)buffer.Length, 0);
        }

        if (length == 0 || length > buffer.Length)
        {
            return null;
        }

        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle handle, char* buffer, uint length, uint flags);
}
