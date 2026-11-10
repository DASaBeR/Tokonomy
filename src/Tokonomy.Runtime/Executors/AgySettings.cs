using System.Text.Json;
using System.Text.RegularExpressions;
using Tokonomy.Core.Config;

namespace Tokonomy.Runtime.Executors;

/// <summary>The per-attempt <c>--settings</c> content for the Antigravity CLI plus what Tokonomy needs to audit the run.</summary>
public sealed record AgySettingsFile(string Json, IReadOnlyList<string> AllowedCommands, IReadOnlyList<string> Notes)
{
    /// <summary>Paths named by read_file/write_file deny rules: a file tool that errors on one of these was refused.</summary>
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];
}

/// <summary>
/// Builds a least-privilege <c>permissions</c> block for a headless Antigravity worker (agy 1.5.3, verified live):
/// <list type="bullet">
/// <item>Rules are literal. <c>command(a b c)</c> allows exactly the command line <c>a b c</c>: no prefix match, and regex
/// characters are not interpreted (<c>(-q|-v)</c>, <c>-[qv]</c>, <c>.*</c> all failed to match). One rule per exact command.</item>
/// <item><c>deny</c> beats <c>allow</c> and matches by path prefix, regardless of how specific the allow is.</item>
/// <item>No edit mode is used. <c>read_file</c>/<c>write_file</c> are granted on the worktree only; everything else
/// outside the worktree is refused by default, except the temp directory, which agy lets any run write.</item>
/// </list>
/// </summary>
public static partial class AgySettings
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Home-relative locations of logins and secrets the worker must never read, including both agents' own.</summary>
    internal static readonly string[] CredentialDirectories =
    [
        ".gemini", ".antigravity", ".claude", ".claude.json", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker",
        ".config", ".git-credentials", ".gitconfig", ".npmrc", ".pypirc", ".netrc",
        Path.Combine("AppData", "Roaming"),
    ];

    public static AgySettingsFile Build(
        string workdir, ExecutorPermissions permissions, IEnumerable<string> gateCommands, string? tempDirectory = null,
        IEnumerable<PathRestriction>? restrictions = null, string? homeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workdir);
        ArgumentNullException.ThrowIfNull(permissions);

        var root = Path.GetFullPath(workdir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var notes = new List<string>();
        var allow = new List<string>();
        var deny = new List<string>();
        var commands = new List<string>();

        if (permissions.ScopeFilesToWorktree)
        {
            allow.Add($"read_file({root})");
            allow.Add($"write_file({root})");
        }

        foreach (var command in permissions.Commands.Concat(permissions.AllowGateCommands ? gateCommands : []))
        {
            var normalized = AgyRunInterpreter.Normalize(command);
            if (normalized.Length == 0 || commands.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normalized.Contains('(') || normalized.Contains(')'))
            {
                notes.Add($"not granted (a rule cannot contain parentheses): {normalized}");
                continue;
            }

            commands.Add(normalized);
            allow.Add($"command({normalized})");
        }

        foreach (var raw in permissions.Allow)
        {
            allow.Add(raw);
            if (CommandRule().Match(raw) is { Success: true } m)
            {
                commands.Add(AgyRunInterpreter.Normalize(m.Groups[1].Value));
            }
        }

        deny.AddRange(permissions.Deny);

        // The worktree's .git pointer and Tokonomy's own directory are never the worker's to write. agy matches deny
        // rules by whole path segment (verified: denying <wt>\.git does not affect <wt>\.gitignore).
        deny.Add($"write_file({Path.Combine(root, ".git")})");
        deny.Add($"write_file({Path.Combine(root, ".tokonomy")})");

        // Links that lead outside the worktree (from the repository or a prepare step) are followed by agy's file
        // tools, because its permission check is lexical; deny the link paths themselves. Ignored content is denied
        // for writing, because changes there are invisible in the diff.
        const int maxRestrictions = 300;
        var restricted = 0;
        foreach (var r in restrictions ?? [])
        {
            if (restricted++ >= maxRestrictions)
            {
                notes.Add($"more than {maxRestrictions} restricted paths; the rest are not denied explicitly (changes there are still detected after the attempt)");
                break;
            }

            var full = Path.Combine(root, r.RelativePath.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            deny.Add($"write_file({full})");
            if (r.DenyRead)
            {
                deny.Add($"read_file({full})");
            }
        }

        // Defense in depth: everything outside the worktree is already refused by default, but credential and agent
        // configuration directories are denied explicitly so a future change in agy's defaults does not expose them.
        // This only affects the model's tool calls; agy itself still reads its own login.
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            foreach (var name in CredentialDirectories)
            {
                var path = Path.Combine(home, name);
                deny.Add($"read_file({path})");
                deny.Add($"write_file({path})");
            }
        }

        if (permissions.DenyTempWrites || permissions.DenyTempReads)
        {
            var temp = Path.GetFullPath(tempDirectory ?? Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (AgyRunInterpreter.Inside(temp, root))
            {
                // A deny on the temp directory would also deny the worktree (deny wins over allow, by path prefix).
                notes.Add($"the worktree is inside the temp directory ({temp}), which agy lets any run read and write: a worker can read and modify other runs' worktrees. Set isolation.worktree_root outside temp.");
            }
            else
            {
                if (permissions.DenyTempWrites)
                {
                    deny.Add($"write_file({temp})");
                }

                if (permissions.DenyTempReads)
                {
                    deny.Add($"read_file({temp})");
                }
            }
        }

        var json = JsonSerializer.Serialize(new { permissions = new { allow, deny } }, Indented);
        var deniedPaths = deny.Select(d => FileRule().Match(d)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new AgySettingsFile(json, commands, notes) { DeniedPaths = deniedPaths };
    }

    [GeneratedRegex(@"^(?:read_file|write_file)\((.*)\)$", RegexOptions.Singleline)]
    private static partial Regex FileRule();

    [GeneratedRegex(@"^command\((.*)\)$", RegexOptions.Singleline)]
    private static partial Regex CommandRule();
}
