using System.Runtime.CompilerServices;
using Tokonomy.Core.Config;

namespace Tokonomy.E2E.Tests;

/// <summary>
/// Runs before any test: sends per-user files (defaults.json, proxy.json) to a throwaway folder, so running the tests
/// (for example `init --yes`) can never overwrite the setup the real user picked.
/// </summary>
internal static class TestUserDirectory
{
    [ModuleInitializer]
    internal static void Redirect() =>
        Environment.SetEnvironmentVariable(UserDirectory.OverrideVariable, Path.Combine(Path.GetTempPath(), "tokonomy-tests-user", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
