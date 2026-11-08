using System.Text.Json;
using System.Text.RegularExpressions;

namespace Taskrail.Runtime.Git;

/// <summary>
/// Detects whether a diff adds a third-party dependency. Deliberately conservative (false positives only cost an
/// architect review). Lockfile changes always count; manifests are inspected for added dependency entries.
/// </summary>
public static partial class DependencyDetector
{
    private static readonly HashSet<string> Lockfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "bun.lock",
        "poetry.lock", "uv.lock", "Pipfile.lock", "pdm.lock",
        "packages.lock.json", "go.sum", "Cargo.lock", "Gemfile.lock", "composer.lock",
    };

    public static async Task<bool> DetectAsync(
        IReadOnlyList<string> changedPaths, string patch, Func<string, Task<string?>> readBase, Func<string, string?> readHead)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentNullException.ThrowIfNull(readBase);
        ArgumentNullException.ThrowIfNull(readHead);

        foreach (var path in changedPaths)
        {
            var name = Path.GetFileName(path);
            if (Lockfiles.Contains(name))
            {
                return true;
            }

            if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase) &&
                NpmDependencyAdded(await readBase(path), readHead(path)))
            {
                return true;
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
                return true;
            }
        }

        return false;
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

    // "requests>=2", 'httpx', or "name[extra]==1.0" as a list item.
    [GeneratedRegex("""^\s*["'][A-Za-z0-9][A-Za-z0-9._-]*(\[[^\]]*\])?\s*([<>=!~;@].*)?["'],?\s*$""")]
    private static partial Regex PythonRequirement();

    [GeneratedRegex(@"^\s*(require\s+)?[a-z0-9.\-]+\.[a-z]{2,}/\S+\s+v\d")]
    private static partial Regex GoRequire();

    [GeneratedRegex(@"(^\s*[A-Za-z0-9_-]+\s*=\s*[""{]|^\s*gem\s+['""]|<artifactId>|^\s*(implementation|api|compile)\b)")]
    private static partial Regex GenericDependency();
}
