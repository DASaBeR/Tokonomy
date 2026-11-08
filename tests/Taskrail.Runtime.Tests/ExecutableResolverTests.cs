using Taskrail.Runtime.Process;

namespace Taskrail.Runtime.Tests;

public class ExecutableResolverTests
{
    // Expected values follow cross-spawn's escaping rules for cmd.exe.
    [Theory]
    [InlineData("simple", "^\"simple^\"")]
    [InlineData("a&b", "^\"a^&b^\"")]
    [InlineData("50%", "^\"50^%^\"")]
    [InlineData("x|y>z", "^\"x^|y^>z^\"")]
    [InlineData("say \"hi\"", "^\"say^ \\^\"hi\\^\"^\"")]
    [InlineData(@"dir\", "^\"dir\\\\^\"")]
    public void Escapes_cmd_metacharacters(string input, string expected) =>
        Assert.Equal(expected, ExecutableResolver.EscapeArgument(input, doubleEscapeMetaChars: false));

    [Fact]
    public void Double_escapes_for_node_modules_bin_shims()
    {
        var args = ExecutableResolver.BuildCmdArguments(@"C:\repo\node_modules\.bin\eslint.cmd", ["a&b"]);

        Assert.Contains("^^^\"a^^^&b^^^\"", args, StringComparison.Ordinal);
        Assert.StartsWith("/d /s /c \"", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolves_relative_paths_against_working_directory()
    {
        using var tmp = new TempDir();
        var name = OperatingSystem.IsWindows() ? "tool.cmd" : "tool";
        tmp.File(Path.Combine("bin", name), "echo hi");

        var resolved = ExecutableResolver.TryResolve(Path.Combine("bin", "tool"), tmp.Path);

        Assert.Equal(Path.Combine(tmp.Path, "bin", name), resolved);
    }

    [Fact]
    public void Returns_null_for_unknown_command() =>
        Assert.Null(ExecutableResolver.TryResolve("taskrail-no-such-tool-xyz", Directory.GetCurrentDirectory()));

    [Fact]
    public async Task Cmd_shim_receives_metacharacter_argument_literally()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var tmp = new TempDir();
        // Like npm shims, the batch file forwards its argument. %1 keeps the quotes, so echo prints it verbatim.
        // If escaping were wrong, cmd would split on & and | and try to run "b" and "c" as commands.
        tmp.File("echoarg.cmd", "@echo %1\r\n");
        var outcome = await new ProcessRunner().RunAsync(
            new ProcessSpec { Args = [Path.Combine(tmp.Path, "echoarg.cmd"), "a&b|c 50%"], WorkingDirectory = tmp.Path }, default);

        Assert.True(string.IsNullOrWhiteSpace(outcome.Stderr), outcome.Stderr);
        Assert.Equal("\"a&b|c 50%\"", outcome.Stdout.Trim());
    }
}
