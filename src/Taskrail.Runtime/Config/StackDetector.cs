using System.Text.Json;
using Taskrail.Core.Config;

namespace Taskrail.Runtime.Config;

public sealed record StackInfo(string Name, string Marker, IReadOnlyList<GateConfig> Gates)
{
    /// <summary>Dependency-restore commands run once per run before the baseline.</summary>
    public IReadOnlyList<GateConfig> Prepare { get; init; } = [];
}

/// <summary>Detects the project stack from marker files at the repo root and proposes default gates.</summary>
public static class StackDetector
{
    private const string TmpDir = ".taskrail/tmp";

    public static IReadOnlyList<StackInfo> Detect(string repoRoot)
    {
        var stacks = new List<StackInfo>();

        var dotnetMarker = FirstMatch(repoRoot, "*.slnx", "*.sln", "*.csproj", "*.fsproj");
        if (dotnetMarker is not null)
        {
            stacks.Add(new("dotnet", dotnetMarker,
            [
                Gate("build", "dotnet", "build", "--nologo"),
                GateWithReport("test", $"{TmpDir}/test-results.trx", "dotnet", "test", "--nologo", "--logger", "trx;LogFileName=test-results.trx", "--results-directory", TmpDir),
                Gate("format", "dotnet", "format", "--verify-no-changes"),
            ])
            {
                Prepare = [Gate("restore", "dotnet", "restore")],
            });
        }

        if (File.Exists(Path.Combine(repoRoot, "package.json")))
        {
            var (hasTest, hasEslint, hasTypescript) = InspectPackageJson(Path.Combine(repoRoot, "package.json"));
            var gates = new List<GateConfig>();
            if (hasTest)
            {
                gates.Add(Gate("test", "npm", "test"));
            }

            if (hasEslint)
            {
                gates.Add(Gate("lint", "npx", "--no-install", "eslint", "."));
            }

            if (hasTypescript)
            {
                gates.Add(Gate("types", "npx", "--no-install", "tsc", "--noEmit"));
            }

            var install = File.Exists(Path.Combine(repoRoot, "package-lock.json"))
                ? Gate("install", "npm", "ci")
                : Gate("install", "npm", "install");
            stacks.Add(new("node", "package.json", gates) { Prepare = [install] });
        }

        if (File.Exists(Path.Combine(repoRoot, "pyproject.toml")))
        {
            stacks.Add(new("python", "pyproject.toml",
            [
                Gate("lint", "ruff", "check", "."),
                Gate("types", "mypy", "."),
                GateWithReport("test", $"{TmpDir}/pytest.xml", "pytest", "-q", $"--junitxml={TmpDir}/pytest.xml"),
            ]));
        }

        if (File.Exists(Path.Combine(repoRoot, "go.mod")))
        {
            stacks.Add(new("go", "go.mod", [Gate("vet", "go", "vet", "./..."), Gate("test", "go", "test", "./...")]));
        }

        if (File.Exists(Path.Combine(repoRoot, "Cargo.toml")))
        {
            stacks.Add(new("rust", "Cargo.toml", [Gate("lint", "cargo", "clippy", "--all-targets"), Gate("test", "cargo", "test")]));
        }

        if (File.Exists(Path.Combine(repoRoot, "pom.xml")))
        {
            stacks.Add(new("maven", "pom.xml", [Gate("test", "mvn", "-q", "test")]));
        }
        else if (File.Exists(Path.Combine(repoRoot, "build.gradle")) || File.Exists(Path.Combine(repoRoot, "build.gradle.kts")))
        {
            stacks.Add(new("gradle", "build.gradle", [Gate("test", "gradle", "test")]));
        }

        return stacks;
    }

    private static GateConfig Gate(string name, params string[] cmd) => new() { Name = name, Cmd = [.. cmd] };

    private static GateConfig GateWithReport(string name, string report, params string[] cmd) =>
        new() { Name = name, Report = report, Cmd = [.. cmd] };

    private static string? FirstMatch(string dir, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var hit = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).FirstOrDefault();
            if (hit is not null)
            {
                return Path.GetFileName(hit);
            }
        }

        return null;
    }

    private static (bool Test, bool Eslint, bool Typescript) InspectPackageJson(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var hasTest = root.TryGetProperty("scripts", out var scripts) && scripts.TryGetProperty("test", out var t) &&
                          !(t.GetString() ?? "").Contains("no test specified", StringComparison.Ordinal);
            return (hasTest, HasDep(root, "eslint"), HasDep(root, "typescript"));
        }
        catch (JsonException)
        {
            return (true, false, false);
        }

        static bool HasDep(JsonElement root, string name) =>
            (root.TryGetProperty("dependencies", out var d) && d.TryGetProperty(name, out _)) ||
            (root.TryGetProperty("devDependencies", out var dd) && dd.TryGetProperty(name, out _));
    }
}
