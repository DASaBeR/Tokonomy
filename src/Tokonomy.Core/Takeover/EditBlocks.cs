using System.Text.RegularExpressions;

namespace Tokonomy.Core.Takeover;

/// <summary>One search/replace edit. An empty <see cref="Search"/> creates (or overwrites) the file with <see cref="Replace"/>.</summary>
public sealed record EditBlock(string Path, string Search, string Replace);

/// <summary>
/// Parses the architect's edit blocks:
/// <code>
/// &lt;&lt;&lt;&lt;&lt;&lt;&lt; SEARCH path/to/file
/// exact existing lines
/// =======
/// replacement lines
/// &gt;&gt;&gt;&gt;&gt;&gt;&gt; REPLACE
/// </code>
/// Search/replace was chosen over unified diffs after replaying real failures: it applied 5/5 times, a diff 4/5.
/// </summary>
public static partial class EditBlocks
{
    public static IReadOnlyList<EditBlock> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return BlockRegex().Matches(normalized)
            .Select(m => new EditBlock(m.Groups[1].Value.Trim(), m.Groups[2].Value, m.Groups[3].Value))
            .ToList();
    }

    [GeneratedRegex(@"<<<<<<< SEARCH ([^\n]+)\n(.*?)\n?=======\n(.*?)\n?>>>>>>> REPLACE", RegexOptions.Singleline)]
    private static partial Regex BlockRegex();
}
