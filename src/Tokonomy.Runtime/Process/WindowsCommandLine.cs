using System.Text;

namespace Tokonomy.Runtime.Process;

/// <summary>
/// Builds a Windows command line from an argument array so that <c>CommandLineToArgvW</c> and the MSVC runtime split
/// it back into exactly those arguments. Used where a command line has to be handed over as text (inside the gate
/// sandbox, where .NET's <c>ArgumentList</c> is not available to the PowerShell 5.1 runner).
/// </summary>
public static class WindowsCommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    public static string Quote(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return arg;
        }

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
