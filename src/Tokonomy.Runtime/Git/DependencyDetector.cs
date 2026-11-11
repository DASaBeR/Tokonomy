using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tokonomy.Runtime.Git;

/// <summary>
/// Detects whether a diff adds a third-party dependency. Deliberately conservative (false positives only cost an
/// architect review). Lockfile changes always count; manifests are inspected for added dependency entries.
/// </summary>
public static partial class DependencyDetector
{
    /// <summary>The package names a diff adds, and whether it also changed dependencies that could not be named.</summary>
    public sealed record Change(IReadOnlyList<string> Names, bool Unnamed)
    {
        public static Change None { get; } = new([], false);

        public bool Any => Unnamed || Names.Count > 0;

        /// <summary>What still needs a human after removing the pre-approved packages (see <c>escalation.allowed_dependencies</c>).</summary>
        public Change Unapproved(IReadOnlyList<string> allowed) =>
            new(Names.Where(n => !IsAllowed(n, allowed)).ToList(), Unnamed);

        internal static bool IsAllowed(string name, IReadOnlyList<string> allowed) =>
            allowed.Any(a => a.EndsWith('*')
                ? name.StartsWith(a[..^1], StringComparison.OrdinalIgnoreCase)
                : string.Equals(name, a, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> Lockfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "bun.lock",
        "poetry.lock", "uv.lock", "Pipfile.lock", "pdm.lock",
        "packages.lock.json", "go.sum", "Cargo.lock", "Gemfile.lock", "composer.lock",
    };

    public static async Task<bool> DetectAsync(
        IReadOnlyList<string> changedPaths, string patch, Func<string, Task<string?>> readBase, Func<string, string?> readHead) =>
        (await FindAsync(changedPaths, patch, readBase, readHead)).Any;

    public static async Task<Change> FindAsync(
        IReadOnlyList<string> changedPaths, string patch, Func<string, Task<string?>> readBase, Func<string, string?> readHead)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentNullException.ThrowIfNull(readBase);
        ArgumentNullException.ThrowIfNull(readHead);

        var names = new List<string>();
        var unnamed = false;

        foreach (var path in changedPaths)
        {
            var name = Path.GetFileName(path);
            if (Lockfiles.Contains(name))
            {
                unnamed = true;
            }

            if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            {
                var old = NpmDeps(await readBase(path));
                var now = readHead(path) is { } after ? NpmDeps(after) : [];
                names.AddRange(now.Where(d => !old.Contains(d)));
            }
        }

        foreach (var (file, line) in AddedLines(patch ?? ""))
        {
            var name = Path.GetFileName(file);
            var isMatch = name switch
            {
                _ when name.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                    => line.Contains("<PackageReference", StringComparison.Ordinal) || line.Contains("<PackageVersion", StringComparison.Ordinal),
                _ when name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'),
                "pyproject.toml" or "setup.cfg" => PythonRequirement().IsMatch(line),
                "go.mod" => GoRequire().IsMatch(line),
                "Cargo.toml" or "Gemfile" or "pom.xml" or "build.gradle" or "build.gradle.kts" => GenericDependency().IsMatch(line),
                _ => false,
            };
            if (isMatch)
            {
                if (PackageName(name, line) is { } package)
                {
                    names.Add(package);
                }
                else
                {
                    unnamed = true;
                }
            }
        }

        return new Change(names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), unnamed);
    }

    /// <summary>The package a manifest line names, where the format makes that reliable; null otherwise.</summary>
    private static string? PackageName(string file, string line)
    {
        if (file.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".props", StringComparison.OrdinalIgnoreCase))
        {
            var m = DotNetPackage().Match(line);
            return m.Success ? m.Groups[1].Value : null;
        }

        if (file.StartsWith("requirements", StringComparison.OrdinalIgnoreCase))
        {
            var m = RequirementsName().Match(line);
            return m.Success ? m.Groups[1].Value : null;
        }

        if (file is "pyproject.toml" or "setup.cfg")
        {
            var m = QuotedPythonName().Match(line);
            return m.Success ? m.Groups[1].Value : null;
        }

        return null;
    }

    internal static bool NpmDependencyAdded(string? before, string? after)
    {
        if (after is null)
        {
            return false;
        }

        var old = NpmDeps(before);
        return NpmDeps(after).Any(d => !old.Contains(d));
    }

    private static HashSet<string> NpmDeps(string? json)
    {
        var deps = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return deps;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
            {
                if (doc.RootElement.TryGetProperty(section, out var obj) && obj.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in obj.EnumerateObject())
                    {
                        deps.Add(p.Name);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return deps;
    }

    private static IEnumerable<(string File, string Line)> AddedLines(string patch)
    {
        string? file = null;
        foreach (var raw in patch.Split('\n'))
        {
            if (raw.StartsWith("+++ ", StringComparison.Ordinal))
            {
                file = raw.StartsWith("+++ b/", StringComparison.Ordinal) ? raw[6..].TrimEnd('\r') : null;
            }
            else if (file is not null && raw.StartsWith('+'))
            {
                yield return (file, raw[1..].TrimEnd('\r'));
            }
        }
    }

    [GeneratedRegex("""<Package(?:Reference|Version)\b[^>]*?\b(?:Include|Update)\s*=\s*"([^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetPackage();

    [GeneratedRegex("""^\s*([A-Za-z0-9][A-Za-z0-9._-]*)""")]
    private static partial Regex RequirementsName();

    [GeneratedRegex("""^\s*["']([A-Za-z0-9][A-Za-z0-9._-]*)""")]
    private static partial Regex QuotedPythonName();

    // "requests>=2", 'httpx', or "name[extra]==1.0" as a list item.
    [GeneratedRegex("""^\s*["'][A-Za-z0-9][A-Za-z0-9._-]*(\[[^\]]*\])?\s*([<>=!~;@].*)?["'],?\s*$""")]
    private static partial Regex PythonRequirement();

    [GeneratedRegex(@"^\s*(require\s+)?[a-z0-9.\-]+\.[a-z]{2,}/\S+\s+v\d")]
    private static partial Regex GoRequire();

    [GeneratedRegex(@"(^\s*[A-Za-z0-9_-]+\s*=\s*[""{]|^\s*gem\s+['""]|<artifactId>|^\s*(implementation|api|compile)\b)")]
    private static partial Regex GenericDependency();
}
