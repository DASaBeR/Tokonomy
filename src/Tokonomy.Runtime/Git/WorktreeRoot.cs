namespace Tokonomy.Runtime.Git;

/// <summary>
/// Where run worktrees live, and which locations are refused.
/// <para>
/// Default: <c>%LOCALAPPDATA%\tokonomy\worktrees</c> on Windows, <c>~/.local/share/tokonomy/worktrees</c> on Linux and
/// <c>~/Library/Application Support/tokonomy/worktrees</c> on macOS (the platform's local application data folder). It is
/// predictable, per user, outside the temp directory (which some agents treat as writable scratch space for every run)
/// and outside the repository (so git, build tools and file watchers in the main checkout never see a worktree).
/// </para>
/// </summary>
public static class WorktreeRoot
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The default root, or null when the platform reports no home or local data folder.</summary>
    public static string? Default()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                return null;
            }

            local = Path.Combine(home, ".local", "share");
        }

        return Path.Combine(local, "tokonomy", "worktrees");
    }

    /// <summary>The configured root resolved against the repository, or the default.</summary>
    public static string? Effective(string? configured, string repoRoot) =>
        string.IsNullOrWhiteSpace(configured)
            ? Default()
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured), Path.GetFullPath(repoRoot));

    /// <summary>
    /// Problems that make <paramref name="root"/> unsafe; empty when it is acceptable. Checked against both the path as
    /// written and the path with any links in it resolved, so a junction that leads into temp is caught too.
    /// </summary>
    public static IReadOnlyList<string> Validate(string? root, string? repoRoot)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return ["no worktree root: the home / local application data folder is unknown; set isolation.worktree_root"];
        }

        var problems = new List<string>();
        var lexical = Normalize(root);
        if (!Path.IsPathFullyQualified(lexical))
        {
            problems.Add($"{root} is not an absolute path");
            return problems;
        }

        var real = RealPath(lexical);
        foreach (var candidate in real is null || string.Equals(real, lexical, PathComparison) ? [lexical] : new[] { lexical, real })
        {
            foreach (var problem in Check(candidate, repoRoot))
            {
                if (!problems.Contains(problem))
                {
                    problems.Add(candidate == lexical ? problem : $"{problem} (after resolving links: {candidate})");
                }
            }
        }

        return problems;
    }

    private static IEnumerable<string> Check(string root, string? repoRoot)
    {
        if (string.Equals(root, Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison) ||
            root.Length <= (Path.GetPathRoot(root)?.Length ?? 0))
        {
            yield return $"{root} is a drive or filesystem root";
        }

        foreach (var temp in TempDirectories())
        {
            if (Overlaps(root, temp))
            {
                yield return $"{root} overlaps the temp directory {temp}: agents such as Antigravity let any run read and write temp";
            }
        }

        if (!string.IsNullOrWhiteSpace(repoRoot) && Overlaps(root, Normalize(repoRoot)))
        {
            yield return $"{root} overlaps the repository {Normalize(repoRoot)}: worktrees must live outside it";
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && InsideOrEqual(Normalize(home), root))
        {
            yield return $"{root} contains the home directory {Normalize(home)}";
        }

        foreach (var sensitive in SensitiveDirectories())
        {
            if (Overlaps(root, sensitive))
            {
                yield return $"{root} overlaps {sensitive}, which holds credentials, settings or system files";
            }
        }
    }

    /// <summary>True when either path is inside (or equal to) the other.</summary>
    public static bool Overlaps(string a, string b) => InsideOrEqual(a, b) || InsideOrEqual(b, a);

    private static bool InsideOrEqual(string inner, string outer) =>
        string.Equals(inner, outer, PathComparison) || inner.StartsWith(outer + Path.DirectorySeparatorChar, PathComparison);

    public static IEnumerable<string> TempDirectories()
    {
        var all = new[]
        {
            Path.GetTempPath(),
            Environment.GetEnvironmentVariable("TEMP"),
            Environment.GetEnvironmentVariable("TMP"),
            Environment.GetEnvironmentVariable("TMPDIR"),
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") : "/tmp",
            OperatingSystem.IsWindows() ? null : "/var/tmp",
        };
        foreach (var t in all.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Normalize(t!)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return t;
            if (RealPath(t) is { } real && !string.Equals(real, t, PathComparison))
            {
                yield return real;   // e.g. macOS /var -> /private/var
            }
        }
    }

    internal static IEnumerable<string> SensitiveDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
        };
        if (OperatingSystem.IsWindows())
        {
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));   // Roaming: app settings and tokens
        }
        else
        {
            folders.AddRange(["/etc", "/usr", "/bin", "/sbin", "/var/lib", "/System", "/Library"]);
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            foreach (var name in new[] { ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".config", ".gemini", ".claude", ".antigravity", ".git-credentials", ".npmrc", ".pypirc", ".netrc" })
            {
                folders.Add(Path.Combine(home, name));
            }
        }

        return folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Normalize(f!)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The path with every link in its existing part resolved; the missing tail is appended unchanged.</summary>
    internal static string? RealPath(string path)
    {
        try
        {
            var existing = path;
            var tail = new Stack<string>();
            while (!Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (parent is null)
                {
                    return null;
                }

                tail.Push(Path.GetFileName(existing));
                existing = parent;
            }

            // Resolve component by component from the top so a link anywhere on the way is followed.
            var root = Path.GetPathRoot(existing)!;
            var current = root;
            foreach (var part in existing[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = target.FullName;
                }
            }

            while (tail.Count > 0)
            {
                current = Path.Combine(current, tail.Pop());
            }

            return Normalize(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
