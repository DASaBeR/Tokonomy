using System.Security.Cryptography;
using System.Text;

namespace Taskrail.Core.Takeover;

/// <summary>
/// Text the architect wrote that later work must not change. The gate checks that the text still appears in the file
/// (line endings normalized), so a lock survives unrelated edits above and below it.
/// </summary>
public sealed record LockedRegion(string Path, string Text)
{
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(Text))));

    public int Lines => Normalize(Text).Split('\n').Length;

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n');
}

/// <summary>What the architect changed during a takeover, and what the next workers must respect.</summary>
public sealed record Handoff
{
    public required string UnitId { get; init; }
    public required string UnitTitle { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required string Summary { get; init; }
    public required string Why { get; init; }
    public required IReadOnlyList<LockedRegion> Locked { get; init; }
    public IReadOnlyList<string> MustFollow { get; init; } = [];
}
