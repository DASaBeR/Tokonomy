using Tokonomy.Core.Model;

namespace Tokonomy.Core.Planning;

/// <summary>Path rules for architect-written acceptance tests: they must stay inside one directory of the repository.</summary>
public static class AcceptancePaths
{
    public const int MaxFileChars = 60_000;

    /// <summary>Normalised repo-relative path with '/' separators, or null when <paramref name="path"/> is not safely inside <paramref name="dir"/>.</summary>
    public static string? Normalize(string dir, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        var p = path.Replace('\\', '/').Trim();
        var d = dir.Replace('\\', '/').Trim().Trim('/');
        if (p.StartsWith('/') || p.Contains(':') || p.Contains('\0') || d.Length == 0 || d.Split('/').Any(s => s is ".." or "." or ""))
        {
            return null;
        }

        if (p.Split('/').Any(s => s is "" or "." or ".." || s.EndsWith(' ') || s.EndsWith('.')))
        {
            return null;
        }

        return p.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) && p.Length > d.Length + 1 ? p : null;
    }

    public static IEnumerable<string> Validate(Contract contract, string dir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var any = false;
        foreach (var unit in contract.Units)
        {
            foreach (var test in unit.Tests)
            {
                any = true;
                if (Normalize(dir, test.Path) is not { } norm)
                {
                    yield return $"unit '{unit.Id}': test path '{test.Path}' must be a relative path inside '{dir}/'";
                    continue;
                }

                if (!seen.Add(norm))
                {
                    yield return $"test path '{norm}' is used twice";
                }

                if (string.IsNullOrWhiteSpace(test.Content) || test.Content.Length > MaxFileChars)
                {
                    yield return $"test '{norm}' is empty or longer than {MaxFileChars} characters";
                }
            }
        }

        if (!any)
        {
            yield return $"acceptance tests are enabled: write at least one executable test file under '{dir}/'";
        }
    }
}
