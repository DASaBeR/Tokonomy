using System.Text.Json;
using System.Text.RegularExpressions;
using Tokonomy.Core.Config;

namespace Tokonomy.Runtime.Executors;

/// <summary>What shell commands one unit's worker may run: nothing, an approved list, or any command.</summary>
public sealed record ShellGrant(IReadOnlyList<string> Commands, bool All = false)
{
    public static ShellGrant None { get; } = new([]);
    public static ShellGrant AnyCommand { get; } = new([], All: true);

    public bool IsNone => !All && Commands.Count == 0;

    public static ShellGrant Of(IEnumerable<string> commands) =>
        new(commands.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
}

/// <summary>How strictly an agent CLI can enforce a <see cref="ShellGrant"/> that lists commands.</summary>
public enum ShellEnforcement
{
    /// <summary>The CLI itself refuses any command not on the list (Claude Code, OpenCode, a custom CLI with <c>shell_grant_args</c>).</summary>
    PerCommand,

    /// <summary>
    /// The CLI can only be switched between "no commands" and "any command" (Antigravity 1.5.8: a command allowed by a rule still
    /// ends the headless session). A grant allows commands for that unit only, and Tokonomy audits what ran afterwards.
    /// </summary>
    UnitWideWithAudit,

    /// <summary>Only a sandbox level can be chosen (Codex); commands run inside it. Nothing is audited per command.</summary>
    SandboxLevel,

    /// <summary>Tokonomy does not know how to give this CLI shell access.</summary>
    Unsupported,
}

/// <summary>Turns a <see cref="ShellGrant"/> into each agent CLI's own switches. Nothing here widens access unless a grant says so.</summary>
public static partial class ShellAccess
{
    public static ShellEnforcement Enforcement(ExecutorDefinition definition) => AllCommandsMode.Cli(definition) switch
    {
        "claude" or "opencode" => ShellEnforcement.PerCommand,
        "agy" => ShellEnforcement.UnitWideWithAudit,
        "codex" => ShellEnforcement.SandboxLevel,
        _ when definition.ShellGrantArgs.Count > 0 => ShellEnforcement.PerCommand,
        _ when definition.AllCommandsArgs.Count > 0 => ShellEnforcement.UnitWideWithAudit,
        _ => ShellEnforcement.Unsupported,
    };

    /// <summary>The executor's argument list for this grant.</summary>
    public static List<string> Apply(ExecutorDefinition definition, List<string> args, ShellGrant grant)
    {
        if (grant.IsNone)
        {
            return args;
        }

        if (grant.All)
        {
            return AllCommandsMode.Apply(definition, args);
        }

        switch (Enforcement(definition))
        {
            case ShellEnforcement.PerCommand when AllCommandsMode.Cli(definition) == "claude":
                // Claude Code refuses a Bash call that matches no allowed pattern when it cannot ask (headless).
                var result = new List<string>(args) { "--allowedTools" };
                result.AddRange(grant.Commands.Select(c => $"Bash({c})"));
                return result;
            case ShellEnforcement.PerCommand when AllCommandsMode.Cli(definition) != "opencode":
                var custom = new List<string>(args);
                foreach (var command in grant.Commands)
                {
                    custom.AddRange(definition.ShellGrantArgs.Select(a => a.Replace("{command}", command, StringComparison.Ordinal)));
                }

                return custom;
            case ShellEnforcement.UnitWideWithAudit:
                return AllCommandsMode.Apply(definition, args);
            default:
                return args;   // OpenCode is configured through its file (see ApplyToFile); Codex keeps its sandbox level
        }
    }

    /// <summary>A file template (OpenCode's <c>opencode.json</c>) adjusted for this grant.</summary>
    public static string ApplyToFile(ExecutorDefinition definition, string relativePath, string content, ShellGrant grant)
    {
        if (grant.IsNone)
        {
            return content;
        }

        if (grant.All)
        {
            return AllCommandsMode.ApplyToFile(definition, relativePath, content);
        }

        if (AllCommandsMode.Cli(definition) != "opencode" || !relativePath.EndsWith("opencode.json", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        // bash becomes a pattern map: everything denied except the approved commands, matched as written.
        var map = new Dictionary<string, string> { ["*"] = "deny" };
        foreach (var command in grant.Commands)
        {
            map[command] = "allow";
        }

        return BashPermission().Replace(content, "\"bash\": " + JsonSerializer.Serialize(map), 1);
    }

    /// <summary>
    /// Commands the worker ran that the grant does not list. Only meaningful when the CLI could not refuse them itself
    /// (<see cref="ShellEnforcement.UnitWideWithAudit"/>); with <see cref="ShellGrant.All"/> or a CLI that enforces, there is nothing to report.
    /// </summary>
    public static IReadOnlyList<string> Violations(ExecutorDefinition definition, ShellGrant grant, IEnumerable<string> ran)
    {
        if (grant.All || grant.IsNone || Enforcement(definition) != ShellEnforcement.UnitWideWithAudit)
        {
            return [];
        }

        var allowed = new HashSet<string>(grant.Commands.Select(AgyRunInterpreter.Normalize), StringComparer.OrdinalIgnoreCase);
        return ran.Where(c => !allowed.Contains(AgyRunInterpreter.Normalize(c))).Distinct(StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex("\"bash\"\\s*:\\s*\"(?:allow|ask|deny)\"")]
    private static partial Regex BashPermission();
}
