using Tokonomy.Runtime.Git;

namespace Tokonomy.Runtime.Gates.Isolation;

/// <summary>
/// Copies the files a gate needs out of a worktree into a folder Tokonomy owns. The environment only ever sees this
/// copy, never the worktree, so nothing it does can change the worktree or reach through it.
/// <list type="bullet">
/// <item>Only the files git lists (tracked plus new non-ignored files): no <c>.git</c>, no <c>.tokonomy</c>, no ignored build output or dependencies.</item>
/// <item>Each file goes through <see cref="WorktreeGuard.CopyFileOut"/>: a link anywhere on the path, a hard-linked file or a path
/// resolving outside the worktree fails the whole snapshot with <see cref="GateSecurityException"/>.</item>
/// <item>Total size is capped, so a worker cannot fill the disk through the snapshot.</item>
/// </list>
/// </summary>
public static class SourceSnapshot
{
    public sealed record Result(int Files, long Bytes);

    public static Result Copy(GateSource source, string destination, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        Directory.CreateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new GateEnvironmentException($"snapshot folder {destination} is not empty");
        }

        long total = 0;
        var files = 0;
        foreach (var relative in source.Files)
        {
            if (IsExcluded(relative))
            {
                continue;
            }

            string target;
            try
            {
                target = WorktreeGuard.ResolveInside(destination, relative);
                var full = WorktreeGuard.ResolveInside(source.Root, relative);
                if (Directory.Exists(full) && !WorktreeGuard.IsLink(full))
                {
                    continue;   // a submodule gitlink: not part of this repository's source
                }

                var copied = WorktreeGuard.CopyFileOut(source.Root, relative, target, maxBytes - total);
                if (copied is null)
                {
                    continue;   // deleted in the worktree but still in the index
                }

                if (copied < 0)
                {
                    throw new GateSecurityException(
                        $"the source snapshot would exceed gate_execution.artifact_limits.max_snapshot_mb ({maxBytes / (1024 * 1024)} MB) at '{relative}'");
                }

                total += copied.Value;
                files++;
            }
            catch (WorktreeEscapeException ex)
            {
                throw new GateSecurityException($"refusing to copy '{relative}' into the gate environment: {ex.Message}");
            }
        }

        return new Result(files, total);
    }

    /// <summary>Tokonomy's state and git metadata never enter an environment, whatever a file list says.</summary>
    public static bool IsExcluded(string relative)
    {
        var first = relative.Replace('\\', '/').Split('/', 2)[0];
        return first.Equals(".git", StringComparison.OrdinalIgnoreCase) || first.Equals(".tokonomy", StringComparison.OrdinalIgnoreCase);
    }
}
