using System.Collections;
using System.Text.RegularExpressions;

namespace Tokonomy.Runtime.Process;

/// <summary>
/// Builds child-process environments.
/// <list type="bullet">
/// <item><b>Workers</b> get a strict allowlist (OS basics and toolchain locations) plus executor-declared variables.</item>
/// <item><b>Gates</b> inherit the parent environment minus anything that looks like a credential.</item>
/// </list>
/// Provider keys never reach either.
/// </summary>
public static partial class EnvironmentBuilder
{
    public static readonly IReadOnlyList<string> WorkerAllowlist =
    [
        // OS and shell basics
        "PATH", "PATHEXT", "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "USER", "USERNAME", "LOGNAME", "SHELL", "TERM",
        "TEMP", "TMP", "TMPDIR", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "TZ",
        "SystemRoot", "SystemDrive", "windir", "ComSpec", "OS", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
        "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "PSModulePath",
        "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "XDG_RUNTIME_DIR",
        // Toolchains
        "DOTNET_ROOT", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "JAVA_HOME", "GOPATH", "GOROOT", "GOCACHE", "GOMODCACHE",
        "CARGO_HOME", "RUSTUP_HOME", "NVM_DIR", "NODE_PATH", "VIRTUAL_ENV", "PYENV_ROOT", "CONDA_PREFIX", "UV_CACHE_DIR",
        // Networking and certificates
        "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS", "REQUESTS_CA_BUNDLE",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy",
    ];

    public static Dictionary<string, string> ForWorker(
        IEnumerable<string> extraAllowed, IReadOnlyDictionary<string, string> executorEnv, IDictionary? parent = null)
    {
        ArgumentNullException.ThrowIfNull(executorEnv);
        var source = Snapshot(parent);
        var allowed = new HashSet<string>(WorkerAllowlist.Concat(extraAllowed ?? []), KeyComparer);
        var env = new Dictionary<string, string>(KeyComparer);
        foreach (var (key, value) in source)
        {
            if (allowed.Contains(key) && !LooksSecret(key) && !LooksSecretValue(value))
            {
                env[key] = value;
            }
        }

        foreach (var (key, value) in executorEnv)
        {
            env[key] = value;
        }

        return env;
    }

    public static Dictionary<string, string> ForGate(IEnumerable<string> alsoRemove, IDictionary? parent = null)
    {
        var remove = new HashSet<string>(alsoRemove ?? [], KeyComparer);
        var env = new Dictionary<string, string>(KeyComparer);
        foreach (var (key, value) in Snapshot(parent))
        {
            if (!remove.Contains(key) && !LooksSecret(key) && !LooksSecretValue(value))
            {
                env[key] = value;
            }
        }

        return env;
    }

    public static bool LooksSecret(string name) => SecretName().IsMatch(name);

    /// <summary>
    /// A value carrying credentials whatever its name: a URL with <c>user:password@</c> (proxy settings, database URLs)
    /// or a connection string with a password. Such values are dropped even from allowlisted names.
    /// </summary>
    public static bool LooksSecretValue(string value) => SecretValue().IsMatch(value);

    private static StringComparer KeyComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static List<KeyValuePair<string, string>> Snapshot(IDictionary? parent)
    {
        parent ??= Environment.GetEnvironmentVariables();
        var list = new List<KeyValuePair<string, string>>();
        foreach (DictionaryEntry entry in parent)
        {
            if (entry.Key is string k && entry.Value is string v)
            {
                list.Add(new(k, v));
            }
        }

        return list;
    }

    // Credentials by name, plus handles that grant access without being a secret themselves: an SSH or GPG agent socket
    // lets any child process sign or authenticate as the user (e.g. push to GitHub).
    [GeneratedRegex(
        @"(API_?KEY|TOKEN|SECRET|PASSWORD|PASSWD|(^|_)PASS($|_)|CREDENTIAL|PRIVATE_KEY|ACCESS_KEY|AUTH|COOKIE|SESSION|" +
        @"DATABASE_URL|CONNECTION_?STRING|(^|_)DSN$|SSH_AUTH_SOCK|SSH_AGENT|GPG_AGENT|GNUPGHOME|KEYCHAIN|_SOCK$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"[a-z][a-z0-9+.\-]*://[^/\s:@]+:[^/\s@]+@|(^|;)\s*(password|pwd)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SecretValue();
}
