using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Tokonomy.Core.Supervision;

/// <summary>
/// Minimal path glob: <c>**</c> matches across directories, <c>*</c> and <c>?</c> within one segment.
/// Paths are repo-relative with forward slashes. A pattern without a slash also matches by file name.
/// </summary>
public static class Glob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static bool IsMatch(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        path = path.Replace('\\', '/').TrimStart('/');
        var normalized = pattern.Replace('\\', '/').TrimStart('/');
        if (!normalized.Contains('/'))
        {
            normalized = "**/" + normalized;
        }

        return Cache.GetOrAdd(normalized, Compile).IsMatch(path);
    }

    public static bool AnyMatch(IEnumerable<string> patterns, string path) => patterns.Any(p => IsMatch(p, path));

    private static Regex Compile(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                {
                    i++;
                    sb.Append("(?:.*/)?");
                }
                else
                {
                    sb.Append(".*");
                }
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        var options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(sb.ToString(), options);
    }
}
