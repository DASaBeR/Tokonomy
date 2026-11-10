using Tokonomy.Core.Config;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Gates.Isolation;

/// <summary>A host folder mapped read-only into the environment at <see cref="SandboxPath"/>.</summary>
public sealed record Toolchain(string HostPath, string SandboxPath);

/// <summary>
/// Which host toolchain folders a gate environment gets. Windows Sandbox boots a clean Windows with no SDKs, so the
/// host's own installations (for example <c>C:\Program Files\dotnet</c>) are mapped in read-only. A folder is refused
/// when mapping it would expose more than a toolchain: anything under the user profile (where credentials and agent logins
/// live), temp, the repository, the worktree or gate-environment roots, a drive root or the Windows directory.
/// </summary>
public static class Toolchains
{
    public const string SandboxBase = @"C:\tk\tools";

    /// <summary>
    /// The configured folders, or (when none are configured) the folders holding each command's executable on the host.
    /// Commands given as a relative path (for example a virtualenv's python) come from the working copy and need no mapping.
    /// </summary>
    public static (IReadOnlyList<Toolchain> Mapped, IReadOnlyList<string> Problems) Resolve(
        GateExecutionConfig config, IEnumerable<IReadOnlyList<string>> commands, string repoRoot, IEnumerable<string>? alsoRefuse = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var problems = new List<string>();
        var folders = new List<string>();
        if (config.Toolchains.Count > 0)
        {
            folders.AddRange(config.Toolchains.Select(t => Environment.ExpandEnvironmentVariables(t)));
        }
        else
        {
            foreach (var cmd in commands.Where(c => c.Count > 0))
            {
                var exe = cmd[0];
                if (exe.Contains('/') || exe.Contains('\\'))
                {
                    continue;
                }

                var resolved = ExecutableResolver.TryResolve(exe, repoRoot);
                if (resolved is null)
                {
                    problems.Add($"'{exe}' was not found on this machine, so there is no toolchain to map into the gate environment; install it or list its folder in gate_execution.toolchains");
                    continue;
                }

                var dir = Path.GetDirectoryName(resolved)!;
                if (IsUnder(dir, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
                {
                    continue;   // cmd, powershell, tar, robocopy: the sandbox has its own Windows
                }

                // git.exe in Git\cmd and python.exe launchers in Scripts need their installation root.
                var name = Path.GetFileName(dir);
                if (name.Equals("cmd", StringComparison.OrdinalIgnoreCase) || name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Scripts", StringComparison.OrdinalIgnoreCase))
                {
                    dir = Path.GetDirectoryName(dir)!;
                }

                folders.Add(dir);
            }
        }

        var mapped = new List<Toolchain>();
        foreach (var folder in folders.Select(f => Path.GetFullPath(f).TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var refused = Validate(folder, repoRoot, alsoRefuse ?? []);
            if (refused is not null)
            {
                problems.Add(refused);
                continue;
            }

            mapped.Add(new Toolchain(folder, $@"{SandboxBase}\t{mapped.Count}"));
        }

        return (mapped, problems);
    }

    /// <summary>Why <paramref name="folder"/> must not be mapped, or null when it may.</summary>
    public static string? Validate(string folder, string repoRoot, IEnumerable<string> alsoRefuse)
    {
        if (!Path.IsPathFullyQualified(folder))
        {
            return $"toolchain folder '{folder}' is not an absolute path";
        }

        if (!Directory.Exists(folder))
        {
            return $"toolchain folder '{folder}' does not exist";
        }

        if (WorktreeGuard.IsLink(folder) || WorktreeRoot.RealPath(folder) is { } real && !string.Equals(real, folder, StringComparison.OrdinalIgnoreCase))
        {
            return $"toolchain folder '{folder}' is or goes through a link; map its real location instead";
        }

        if (folder.Length <= (Path.GetPathRoot(folder)?.TrimEnd('\\').Length ?? 0) + 1)
        {
            return $"toolchain folder '{folder}' is a drive root";
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var refused = new List<(string Path, string Why)>
        {
            (home, "your user profile (agent logins, SSH keys and other credentials live there)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "the Windows directory"),
            (Path.GetFullPath(repoRoot), "the repository"),
        };
        refused.AddRange(WorktreeRoot.TempDirectories().Select(t => (t, "the temp directory")));
        refused.AddRange(alsoRefuse.Select(p => (p, "a Tokonomy working folder")));
        foreach (var (path, why) in refused.Where(r => !string.IsNullOrWhiteSpace(r.Path)))
        {
            if (WorktreeRoot.Overlaps(folder, Path.GetFullPath(path).TrimEnd('\\', '/')))
            {
                return $"toolchain folder '{folder}' overlaps {why} ({path}); install the toolchain outside it (for example under C:\\Program Files) and list it in gate_execution.toolchains";
            }
        }

        return null;
    }

    private static bool IsUnder(string path, string root) =>
        !string.IsNullOrWhiteSpace(root) && WorktreeRoot.Overlaps(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'));
}
