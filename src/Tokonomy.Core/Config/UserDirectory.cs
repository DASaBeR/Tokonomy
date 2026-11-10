namespace Tokonomy.Core.Config;

/// <summary>
/// Where Tokonomy keeps per-user files (the proxy setting, the last setup you picked): <c>%LOCALAPPDATA%\tokonomy</c>,
/// or the folder named by the <c>TOKONOMY_USER_DIR</c> environment variable. The tests point it at a temp folder so a
/// test run can never overwrite what the real user chose.
/// </summary>
public static class UserDirectory
{
    public const string OverrideVariable = "TOKONOMY_USER_DIR";

    public static string Path { get; } = Resolve();

    private static string Resolve()
    {
        var custom = Environment.GetEnvironmentVariable(OverrideVariable);
        return string.IsNullOrWhiteSpace(custom)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tokonomy")
            : System.IO.Path.GetFullPath(custom);
    }
}
