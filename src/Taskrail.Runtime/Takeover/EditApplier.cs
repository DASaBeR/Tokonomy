using System.Text;
using Taskrail.Core.Takeover;

namespace Taskrail.Runtime.Takeover;

public sealed record ApplyResult(bool Ok, IReadOnlyList<string> Files, IReadOnlyList<string> Errors);

/// <summary>
/// Applies search/replace edits to a worktree. All-or-nothing: every block is checked first and nothing is written
/// unless all of them apply. A SEARCH must match exactly once; if it matches nowhere, a second try ignores trailing
/// whitespace and line-ending differences (still requiring exactly one match). Edits may not leave the worktree or
/// touch <c>.git</c> / <c>.taskrail</c>.
/// </summary>
public static class EditApplier
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static ApplyResult Apply(string workdir, IReadOnlyList<EditBlock> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            return new ApplyResult(false, [], ["no edit blocks found in the reply"]);
        }

        var root = Path.GetFullPath(workdir);
        var pending = new Dictionary<string, string>(PathComparer);
        var files = new List<string>();
        var errors = new List<string>();

        foreach (var edit in edits)
        {
            var relative = edit.Path.Replace('\\', '/').TrimStart('/');
            if (!IsAllowed(relative))
            {
                errors.Add($"{edit.Path}: this path may not be edited");
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            {
                errors.Add($"{edit.Path}: path is outside the repository");
                continue;
            }

            if (string.IsNullOrWhiteSpace(edit.Search))
            {
                pending[full] = edit.Replace.EndsWith('\n') ? edit.Replace : edit.Replace + "\n";
                files.Add(relative);
                continue;
            }

            var current = pending.TryGetValue(full, out var staged) ? staged : (File.Exists(full) ? File.ReadAllText(full) : null);
            if (current is null)
            {
                errors.Add($"{relative}: file not found (use an empty SEARCH to create a file)");
                continue;
            }

            var updated = ReplaceOnce(current, edit.Search, edit.Replace, out var error);
            if (updated is null)
            {
                errors.Add($"{relative}: {error}");
                continue;
            }

            pending[full] = updated;
            files.Add(relative);
        }

        if (errors.Count > 0)
        {
            return new ApplyResult(false, [], errors);
        }

        foreach (var (path, content) in pending)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        return new ApplyResult(true, files.Distinct(StringComparer.Ordinal).ToList(), []);
    }

    internal static string? ReplaceOnce(string content, string search, string replace, out string error)
    {
        error = "";
        var exact = Count(content, search);
        if (exact == 1)
        {
            var index = content.IndexOf(search, StringComparison.Ordinal);
            return string.Concat(content.AsSpan(0, index), replace, content.AsSpan(index + search.Length));
        }

        if (exact > 1)
        {
            error = $"SEARCH matched {exact} times; include more surrounding lines so it is unique";
            return null;
        }

        // Tolerant second try: same lines, ignoring trailing whitespace and CRLF vs LF.
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var wanted = search.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
        while (wanted.Count > 0 && wanted[^1].Length == 0)
        {
            wanted.RemoveAt(wanted.Count - 1);
        }

        var matches = new List<int>();
        for (var i = 0; wanted.Count > 0 && i + wanted.Count <= lines.Count; i++)
        {
            var all = true;
            for (var j = 0; j < wanted.Count && all; j++)
            {
                all = lines[i + j].TrimEnd() == wanted[j];
            }

            if (all)
            {
                matches.Add(i);
            }
        }

        if (matches.Count == 0)
        {
            error = "SEARCH text was not found in the file";
            return null;
        }

        if (matches.Count > 1)
        {
            error = $"SEARCH matched {matches.Count} times (ignoring trailing whitespace); include more surrounding lines";
            return null;
        }

        var replacement = replace.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = lines.Take(matches[0]).Concat(replacement).Concat(lines.Skip(matches[0] + wanted.Count));
        return string.Join(newline, result);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static bool IsAllowed(string relative)
    {
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relative.Split('/');
        return !segments.Contains("..") &&
               !segments[0].Equals(".git", PathComparison) &&
               !segments[0].Equals(".taskrail", PathComparison);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
