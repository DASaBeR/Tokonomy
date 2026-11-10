using System.Text;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;

namespace Tokonomy.Runtime.Gates;

/// <summary>
/// Owns the architect's acceptance test files inside a worktree. The worker may read them but whatever it does to them
/// is undone before every gate run, so the tests that decide acceptance are always exactly what the architect wrote.
/// </summary>
public sealed class AcceptanceFiles(string dir)
{
    public const string GateName = "acceptance";

    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);

    public string Dir { get; } = dir.Replace('\\', '/').Trim().Trim('/');
    public int Count => _files.Count;

    /// <summary>Repo-relative paths of the enforced files, for the worker brief.</summary>
    public IReadOnlyList<string> Paths => _files.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>Adds the unit's tests to the set that is enforced from now on. Returns the files that are new.</summary>
    public IReadOnlyList<string> Add(WorkUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var added = new List<string>();
        foreach (var test in unit.Tests)
        {
            if (AcceptancePaths.Normalize(Dir, test.Path) is { } path)
            {
                _files[path] = test.Content;
                added.Add(path);
            }
        }

        return added;
    }

    /// <summary>
    /// Writes every enforced file, and deletes anything else in the acceptance directory. Returns what the worker had
    /// changed, added or removed, for the record.
    /// </summary>
    public IReadOnlyList<string> Enforce(string workdir)
    {
        var tampered = new List<string>();
        var root = Path.GetFullPath(Path.Combine(workdir, Dir));
        if (_files.Count == 0)
        {
            return tampered;
        }

        if (Directory.Exists(root) && new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(root, recursive: false);
            tampered.Add(Dir + "/ (was a link)");
        }

        Directory.CreateDirectory(root);
        var keep = new HashSet<string>(_files.Keys.Select(k => Path.GetFullPath(Path.Combine(workdir, k))), StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!keep.Contains(Path.GetFullPath(file)) && !IsCacheArtifact(file))
            {
                File.Delete(file);
                tampered.Add(Relative(workdir, file) + " (not an architect file)");
            }
        }

        foreach (var (path, content) in _files)
        {
            var full = Path.GetFullPath(Path.Combine(workdir, path));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var expected = Normalize(content);
            if (!File.Exists(full))
            {
                if (_written.Contains(path))
                {
                    tampered.Add(path + " (deleted)");
                }
            }
            else if (Normalize(File.ReadAllText(full)) != expected)
            {
                tampered.Add(path + " (modified)");
            }

            File.WriteAllText(full, expected, new UTF8Encoding(false));
            _written.Add(path);
        }

        return tampered;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    // The test runner's own byproducts are not worker edits, and stay out of commits through the repository's ignore rules.
    private static bool IsCacheArtifact(string file) =>
        file.Contains("__pycache__", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase);

    private static string Relative(string workdir, string file) => Path.GetRelativePath(workdir, file).Replace('\\', '/');
}
