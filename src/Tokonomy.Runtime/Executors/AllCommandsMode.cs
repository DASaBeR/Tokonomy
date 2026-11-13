using System.Text.RegularExpressions;
using Tokonomy.Core.Config;

namespace Tokonomy.Runtime.Executors;

/// <summary>
/// How each supported agent CLI is switched to "any shell command" when <c>permissions.allow_all_commands</c> is on. The
/// mapping is by the executable (<c>claude</c>, <c>codex</c>, <c>agy</c>, <c>opencode</c>); any other CLI declares its own
/// <c>all_commands_args</c>. Nothing here runs unless the user chose it.
/// </summary>
public static partial class AllCommandsMode
{
    /// <summary>The executor's argument list with the no-restriction switch applied.</summary>
    public static List<string> Apply(ExecutorDefinition definition, List<string> args)
    {
        var result = new List<string>(args);
        switch (Cli(definition))
        {
            case "agy":
                // agy 1.5.8, verified live: with --sandbox ("terminal restrictions enabled") the first shell command ends the
                // session (exit 2, no output); without it the same command runs. "Any command" and a terminal sandbox
                // contradict each other, so the sandbox flag is dropped in this mode.
                result.RemoveAll(a => string.Equals(a, "--sandbox", StringComparison.Ordinal));
                Append(result, "--dangerously-skip-permissions");
                break;
            case "claude":
                if (!ReplaceValue(result, "--permission-mode", "bypassPermissions"))
                {
                    result.AddRange(["--permission-mode", "bypassPermissions"]);
                }

                break;
            case "codex":
                if (!ReplaceValue(result, "--sandbox", "danger-full-access"))
                {
                    result.InsertRange(Math.Min(2, result.Count), ["--sandbox", "danger-full-access"]);
                }

                break;
        }

        foreach (var extra in definition.AllCommandsArgs)
        {
            Append(result, extra);
        }

        return result;
    }

    /// <summary>
    /// A file template (OpenCode's <c>opencode.json</c>) with the permission block opened up. Other files are unchanged.
    /// OpenCode already allows <c>bash</c>; this also allows web fetches and paths outside the worktree.
    /// </summary>
    public static string ApplyToFile(ExecutorDefinition definition, string relativePath, string content)
    {
        if (Cli(definition) != "opencode" || !relativePath.EndsWith("opencode.json", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        return PermissionValue().Replace(content, m => $"\"{m.Groups[1].Value}\": \"allow\"");
    }

    /// <summary>True for a CLI Tokonomy knows how to switch, or one that declares <c>all_commands_args</c>.</summary>
    public static bool IsSupported(ExecutorDefinition definition) =>
        Cli(definition) is "agy" or "claude" or "codex" or "opencode" || definition.AllCommandsArgs.Count > 0;

    public static string Cli(ExecutorDefinition definition) =>
        Path.GetFileNameWithoutExtension(definition.Cmd.FirstOrDefault() ?? "").ToLowerInvariant();

    private static void Append(List<string> args, string value)
    {
        if (!args.Contains(value, StringComparer.Ordinal))
        {
            args.Add(value);
        }
    }

    private static bool ReplaceValue(List<string> args, string flag, string value)
    {
        var i = args.FindIndex(a => string.Equals(a, flag, StringComparison.Ordinal));
        if (i < 0 || i + 1 >= args.Count)
        {
            return false;
        }

        args[i + 1] = value;
        return true;
    }

    [GeneratedRegex(@"""(webfetch|external_directory|bash|edit)""\s*:\s*""(?:deny|ask|allow)""")]
    private static partial Regex PermissionValue();
}
