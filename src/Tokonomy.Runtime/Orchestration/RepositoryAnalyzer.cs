using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tokonomy.Runtime.Git;

namespace Tokonomy.Runtime.Orchestration;

/// <summary>What Tokonomy collected about the repository for planning: the text for the architect and the file set for grounding checks.</summary>
public sealed record RepoInventory(string Context, IReadOnlySet<string> Files, IReadOnlyList<string> RelevantFiles);

/// <summary>
/// Builds the architect's view of the repository without sending the whole repository: a lightweight inventory
/// (languages, manifests, entry points, tests, docs), the files most relevant to the task with their contents, and the
/// state of the working copy. Everything is read from the base commit through git, so nothing outside the repository
/// (and nothing untracked) is ever read. These are facts collected by Tokonomy, marked as such for the architect.
/// </summary>
public static partial class RepositoryAnalyzer
{
    public const int MaxListedFiles = 400;
    public const int MaxRelevantFiles = 8;
    public const int MaxFileChars = 6_000;
    public const int MaxManifestChars = 2_500;
    public const int MaxReadmeChars = 4_000;
    public const int MaxReadChars = 16_000;

    private static readonly string[] ManifestNames =
    [
        "package.json", "pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "go.mod", "Cargo.toml", "pom.xml",
        "build.gradle", "build.gradle.kts", "Gemfile", "composer.json", "Directory.Build.props", "Directory.Packages.props",
        "global.json", "tsconfig.json", "Makefile", "Dockerfile", "docker-compose.yml",
    ];

    private static readonly string[] ManifestExtensions = [".csproj", ".fsproj", ".sln", ".slnx"];

    private static readonly string[] EntryPointNames =
    [
        "Program.cs", "Startup.cs", "main.py", "__main__.py", "app.py", "manage.py", "wsgi.py", "server.js", "index.js",
        "index.ts", "main.ts", "main.go", "main.rs", "lib.rs", "App.tsx", "App.jsx",
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "add", "make", "use", "using", "new", "should", "must",
        "when", "then", "than", "are", "was", "will", "not", "all", "any", "each", "every", "its", "has", "have", "can",
        "also", "only", "same", "keep", "file", "files", "code", "test", "tests", "unit", "support", "implement", "change",
        "update", "existing", "return", "returns", "true", "false", "none", "null", "list", "dict", "string", "int", "value",
    };

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#", [".fs"] = "F#", [".py"] = "Python", [".js"] = "JavaScript", [".jsx"] = "JavaScript", [".ts"] = "TypeScript",
        [".tsx"] = "TypeScript", [".go"] = "Go", [".rs"] = "Rust", [".java"] = "Java", [".kt"] = "Kotlin", [".rb"] = "Ruby",
        [".php"] = "PHP", [".swift"] = "Swift", [".c"] = "C", [".h"] = "C/C++", [".cpp"] = "C++", [".html"] = "HTML", [".css"] = "CSS",
        [".scss"] = "CSS", [".sql"] = "SQL", [".ps1"] = "PowerShell", [".sh"] = "Shell", [".vue"] = "Vue", [".svelte"] = "Svelte",
    };

    public static async Task<RepoInventory> AnalyzeAsync(
        string repoRoot, string baseSha, string task, GitCli git, CancellationToken ct, int maxChars = 60_000)
    {
        ArgumentNullException.ThrowIfNull(git);
        var files = (await git.RunAsync(repoRoot, ["ls-tree", "-r", "--name-only", baseSha], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fileSet = files.ToHashSet(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("(Everything below was collected by Tokonomy from the repository at the base commit; it is verified, not a model's guess.)");
        sb.AppendLine();

        // Inventory.
        var languages = files.GroupBy(f => Languages.GetValueOrDefault(Path.GetExtension(f)) ?? "")
            .Where(g => g.Key.Length > 0).OrderByDescending(g => g.Count()).Take(6)
            .Select(g => $"{g.Key} ({g.Count()})").ToList();
        var manifests = files.Where(IsManifest).Take(20).ToList();
        var entryPoints = files.Where(f => EntryPointNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).Take(10).ToList();
        var testDirs = files.Select(f => TestRoot(f)).OfType<string>().Distinct(StringComparer.Ordinal).Take(10).ToList();
        var docs = files.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Take(15).ToList();
        sb.AppendLine("## Inventory");
        Line(sb, "Languages", languages);
        Line(sb, "Build and dependency manifests", manifests);
        Line(sb, "Entry points", entryPoints);
        Line(sb, "Test locations", testDirs);
        Line(sb, "Documentation", docs);
        sb.AppendLine();

        // Working copy: changes that are not part of the plan's base.
        var status = (await git.RunAsync(repoRoot, ["status", "--porcelain", "--untracked-files=normal"], ct, allowFailure: true))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.EndsWith(".tokonomy/", StringComparison.Ordinal) && !l.Contains(" .tokonomy/", StringComparison.Ordinal))
            .ToList();
        if (status.Count > 0)
        {
            sb.AppendLine("## Uncommitted changes (NOT part of the base commit the work starts from)");
            foreach (var s in status.Take(20))
            {
                sb.AppendLine(s);
            }

            sb.AppendLine();
        }

        sb.AppendLine($"## Files ({files.Length} tracked{(files.Length > MaxListedFiles ? $", first {MaxListedFiles} shown" : "")})");
        foreach (var f in files.Take(MaxListedFiles))
        {
            sb.AppendLine(f);
        }

        // Relevant files, by task terms in their paths and in their content.
        var relevant = await RankAsync(repoRoot, baseSha, task, files, git, ct);
        var budget = Math.Max(10_000, maxChars - sb.Length);
        var included = new List<string>();
        if (relevant.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Files most relevant to the task (contents)");
            foreach (var path in relevant)
            {
                if (budget <= 1000 || await git.ShowFileAsync(repoRoot, baseSha, path, ct) is not { } text || LooksBinary(text))
                {
                    continue;
                }

                var shown = Cap(text, Math.Min(MaxFileChars, budget));
                sb.AppendLine($"### {path}").AppendLine("```").AppendLine(shown.TrimEnd()).AppendLine("```");
                budget -= shown.Length + path.Length + 20;
                included.Add(path);
            }
        }

        foreach (var manifest in manifests.Where(m => !included.Contains(m)).Take(4))
        {
            if (budget <= 1000 || await git.ShowFileAsync(repoRoot, baseSha, manifest, ct) is not { } text || LooksBinary(text))
            {
                continue;
            }

            var shown = Cap(text, Math.Min(MaxManifestChars, budget));
            sb.AppendLine().AppendLine($"## {manifest}").AppendLine("```").AppendLine(shown.TrimEnd()).AppendLine("```");
            budget -= shown.Length;
        }

        var readme = files.FirstOrDefault(f => f.Equals("README.md", StringComparison.OrdinalIgnoreCase) || f.Equals("README", StringComparison.OrdinalIgnoreCase));
        if (readme is not null && budget > 1000 && await git.ShowFileAsync(repoRoot, baseSha, readme, ct) is { } readmeText)
        {
            sb.AppendLine();
            sb.AppendLine($"## {readme}");
            sb.AppendLine(Cap(readmeText, Math.Min(MaxReadmeChars, budget)));
        }

        return new RepoInventory(sb.ToString(), fileSet, included);
    }

    /// <summary>
    /// Contents of files the architect asked to read. Only tracked files at the base commit are served (read through
    /// git, so no path can reach outside the repository); anything else is reported as unavailable.
    /// </summary>
    public static async Task<string> ReadFilesAsync(
        string repoRoot, string baseSha, IReadOnlySet<string> tracked, IReadOnlyList<string> paths, GitCli git, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        var sb = new StringBuilder();
        var budget = MaxReadChars * 4;
        foreach (var path in paths)
        {
            if (!tracked.Contains(path))
            {
                sb.AppendLine($"### {path}").AppendLine("(not a tracked file in the repository)").AppendLine();
                continue;
            }

            var text = await git.ShowFileAsync(repoRoot, baseSha, path, ct);
            if (text is null || LooksBinary(text))
            {
                sb.AppendLine($"### {path}").AppendLine("(binary or unreadable)").AppendLine();
                continue;
            }

            var shown = Cap(text, Math.Min(MaxReadChars, Math.Max(500, budget)));
            budget -= shown.Length;
            sb.AppendLine($"### {path}").AppendLine("```").AppendLine(shown.TrimEnd()).AppendLine("```").AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Distinct, meaningful words of the task (identifiers are split on camelCase and underscores too).</summary>
    internal static IReadOnlyList<string> Terms(string task)
    {
        var terms = new List<string>();
        foreach (Match m in Word().Matches(task))
        {
            var word = m.Value;
            foreach (var part in new[] { word }.Concat(CamelParts().Split(word)).Concat(word.Split('_', '-', '.')))
            {
                var t = part.ToLowerInvariant();
                if (t.Length >= 3 && !StopWords.Contains(t) && !t.All(char.IsDigit) && !terms.Contains(t))
                {
                    terms.Add(t);
                }
            }
        }

        return terms.Take(16).ToList();
    }

    private static async Task<List<string>> RankAsync(string repoRoot, string baseSha, string task, IReadOnlyList<string> files, GitCli git, CancellationToken ct)
    {
        var terms = Terms(task);
        if (terms.Count == 0)
        {
            return [];
        }

        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in files.Where(IsSource))
        {
            var lower = file.ToLowerInvariant();
            var name = Path.GetFileNameWithoutExtension(lower);
            var score = terms.Sum(t => name == t ? 6 : name.Contains(t, StringComparison.Ordinal) ? 4 : lower.Contains(t, StringComparison.Ordinal) ? 2 : 0);
            if (score > 0)
            {
                scores[file] = score;
            }
        }

        // Content hits: one git grep over the base commit, counting matching terms per file.
        var args = new List<string> { "grep", "-I", "-i", "-c", "-F" };
        foreach (var t in terms)
        {
            args.AddRange(["-e", t]);
        }

        args.Add(baseSha);
        var output = await git.RunAsync(repoRoot, args, ct, allowFailure: true);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // <sha>:<path>:<count>
            var last = line.LastIndexOf(':');
            var first = line.IndexOf(':', StringComparison.Ordinal);
            if (first < 0 || last <= first || !int.TryParse(line[(last + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                continue;
            }

            var path = line[(first + 1)..last];
            if (IsSource(path))
            {
                scores[path] = scores.GetValueOrDefault(path) + Math.Min(5, count);
            }
        }

        return scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key.Length).ThenBy(s => s.Key, StringComparer.Ordinal)
            .Take(MaxRelevantFiles).Select(s => s.Key).ToList();
    }

    private static bool IsManifest(string path) =>
        ManifestNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) ||
        ManifestExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ||
        path.StartsWith(".github/workflows/", StringComparison.Ordinal);

    private static bool IsSource(string path) =>
        Languages.ContainsKey(Path.GetExtension(path)) && !path.StartsWith(".tokonomy/", StringComparison.Ordinal) &&
        !path.Contains("/node_modules/", StringComparison.Ordinal) && !path.Contains("/vendor/", StringComparison.Ordinal);

    private static string? TestRoot(string path)
    {
        var parts = path.Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var p = parts[i].ToLowerInvariant();
            if (p is "test" or "tests" or "spec" or "__tests__" || p.EndsWith(".tests", StringComparison.Ordinal))
            {
                return string.Join('/', parts.Take(i + 1)) + "/";
            }
        }

        return null;
    }

    private static void Line(StringBuilder sb, string label, List<string> items)
    {
        if (items.Count > 0)
        {
            sb.AppendLine($"- {label}: {string.Join(", ", items)}");
        }
    }

    private static bool LooksBinary(string text) => text.Contains('\0', StringComparison.Ordinal);

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max] + "\n... (truncated)";

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_\-.]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelParts();
}
