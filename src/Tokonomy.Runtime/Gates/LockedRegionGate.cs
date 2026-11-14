using Tokonomy.Core.Model;
using Tokonomy.Core.Takeover;
using Tokonomy.Runtime.Git;

namespace Tokonomy.Runtime.Gates;

/// <summary>
/// Deterministic check that text the architect wrote is still in place: the file must still contain the locked text
/// exactly (line endings normalised). Enforcement is by content, not by line number, so unrelated edits above or below a
/// locked region do not matter. Files are read through <see cref="WorktreeGuard"/>, so a link planted in the worktree
/// can never satisfy a lock with text from outside it.
/// </summary>
public static class LockedRegionGate
{
    public const string Name = "locked_regions";

    public static GateResult Evaluate(string workdir, IReadOnlyList<LockedRegion> locks)
    {
        ArgumentNullException.ThrowIfNull(locks);
        var broken = new List<string>();
        var escaped = new List<string>();
        foreach (var region in locks)
        {
            string? content;
            try
            {
                content = WorktreeGuard.ReadAllText(workdir, region.Path)?.Replace("\r\n", "\n", StringComparison.Ordinal);
            }
            catch (WorktreeEscapeException)
            {
                escaped.Add(region.Path);
                continue;
            }

            if (content is null || !content.Contains(LockedRegion.Normalize(region.Text), StringComparison.Ordinal))
            {
                broken.Add(region.Path);
            }
        }

        if (broken.Count == 0 && escaped.Count == 0)
        {
            return new GateResult { Gate = Name, Status = GateStatus.Passed };
        }

        var output = new List<string>();
        if (broken.Count > 0)
        {
            output.Add($"Locked regions were changed or removed in: {string.Join(", ", broken.Distinct(StringComparer.Ordinal))}. " +
                       "The architect fixed these lines in an earlier takeover; restore them exactly as they were and make your change elsewhere.");
        }

        if (escaped.Count > 0)
        {
            output.Add($"Locked files are reached through a link, which is not allowed: {string.Join(", ", escaped.Distinct(StringComparer.Ordinal))}. " +
                       "Remove the link and restore the real file.");
        }

        return new GateResult { Gate = Name, Status = GateStatus.Failed, Output = string.Join('\n', output) };
    }
}
