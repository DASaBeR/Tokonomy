using System.Text;
using System.Text.RegularExpressions;

namespace Tokonomy.Runtime.Process;

/// <summary>
/// Resolves a command name to a file on disk and, on Windows, builds a safe <c>cmd.exe</c> invocation for
/// <c>.cmd</c>/<c>.bat</c> targets (e.g. npm-installed CLIs). Escaping follows the cross-spawn rules.
/// </summary>
public static partial class ExecutableResolver
{
    public static string? TryResolve(string command, string workingDirectory, IReadOnlyDictionary<string, string>? env = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (command.Contains('/') || command.Contains('\\'))
        {
            var full = Path.GetFullPath(command, workingDirectory);
            return OperatingSystem.IsWindows() ? ProbeWindowsExtensions(full) : (File.Exists(full) ? full : null);
        }

        var pathVar = Lookup(env, "PATH") ?? System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim('"'), command);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var hit = OperatingSystem.IsWindows() ? ProbeWindowsExtensions(candidate) : (File.Exists(candidate) ? candidate : null);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    public static string Resolve(string command, string workingDirectory, IReadOnlyDictionary<string, string>? env = null)
        => TryResolve(command, workingDirectory, env) ?? throw new ExecutableNotFoundException(command);

    public static bool NeedsCmdWrapper(string resolvedPath) =>
        OperatingSystem.IsWindows() &&
        (resolvedPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
         resolvedPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds the argument string for <c>cmd.exe /d /s /c "..."</c>.</summary>
    public static string BuildCmdArguments(string resolvedPath, IEnumerable<string> args)
    {
        var doubleEscape = CmdShimRegex().IsMatch(resolvedPath);
        var sb = new StringBuilder();
        sb.Append(EscapeCommand(resolvedPath));
        foreach (var arg in args)
        {
            sb.Append(' ').Append(EscapeArgument(arg, doubleEscape));
        }

        return $"/d /s /c \"{sb}\"";
    }

    internal static string EscapeCommand(string command) => MetaChars().Replace(command, "^$1");

    internal static string EscapeArgument(string arg, bool doubleEscapeMetaChars)
    {
        // Backslashes before a quote are doubled and the quote escaped; trailing backslashes are doubled.
        arg = QuoteWithBackslashes().Replace(arg, m => m.Groups[1].Value + m.Groups[1].Value + "\\\"");
        arg = TrailingBackslashes().Replace(arg, m => m.Groups[1].Value + m.Groups[1].Value);
        arg = $"\"{arg}\"";
        arg = MetaChars().Replace(arg, "^$1");
        if (doubleEscapeMetaChars)
        {
            arg = MetaChars().Replace(arg, "^$1");
        }

        return arg;
    }

    private static string? ProbeWindowsExtensions(string candidate)
    {
        if (Path.HasExtension(candidate) && File.Exists(candidate))
        {
            return candidate;
        }

        var pathext = System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        foreach (var ext in pathext.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var withExt = candidate + ext.ToLowerInvariant();
            if (File.Exists(withExt))
            {
                return withExt;
            }
        }

        return null;
    }

    private static string? Lookup(IReadOnlyDictionary<string, string>? env, string key)
    {
        if (env is null)
        {
            return null;
        }

        foreach (var (k, v) in env)
        {
            if (string.Equals(k, key, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return v;
            }
        }

        return null;
    }

    [GeneratedRegex(@"([()\][%!^""`<>&|;, *?])")]
    private static partial Regex MetaChars();

    [GeneratedRegex(@"(\\*)""")]
    private static partial Regex QuoteWithBackslashes();

    [GeneratedRegex(@"(\\*)$")]
    private static partial Regex TrailingBackslashes();

    [GeneratedRegex(@"node_modules[\\/]\.bin[\\/][^\\/]+\.cmd$", RegexOptions.IgnoreCase)]
    private static partial Regex CmdShimRegex();
}
