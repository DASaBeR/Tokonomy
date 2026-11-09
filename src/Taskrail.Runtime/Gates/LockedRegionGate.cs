using Taskrail.Core.Model;
using Taskrail.Core.Takeover;

namespace Taskrail.Runtime.Gates;

/// <summary>
/// Deterministic check that text the architect wrote is still in place. Enforcement is by content, not by line
/// number, so unrelated edits above or below a locked region do not matter.
/// </summary>
public static class LockedRegionGate
{
    public const string Name = "locked_regions";

    public static GateResult Evaluate(string workdir, IReadOnlyList<LockedRegion> locks)
    {
        ArgumentNullException.ThrowIfNull(locks);
        var broken = new List<string>();
        foreach (var region in locks)
        {
            var path = Path.Combine(workdir, region.Path);
            var content = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
            if (content is null || !content.Contains(LockedRegion.Normalize(region.Text), StringComparison.Ordinal))
            {
                broken.Add(region.Path);
            }
        }

        if (broken.Count == 0)
        {
            return new GateResult { Gate = Name, Status = GateStatus.Passed };
        }

        var files = string.Join(", ", broken.Distinct(StringComparer.Ordinal));
        return new GateResult
        {
            Gate = Name,
            Status = GateStatus.Failed,
            Output = $"Locked regions were changed or removed in: {files}. The architect fixed these lines in an earlier takeover; " +
                     "restore them exactly as they were and make your change elsewhere.",
        };
    }
}
