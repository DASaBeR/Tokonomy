using Taskrail.Runtime.Process;

namespace Taskrail.Runtime.Tests;

public class ProcessRunnerTests
{
    private readonly ProcessRunner _runner = new();

    private static string Cwd => Directory.GetCurrentDirectory();

    private static string[] Shell(string unixScript, string windowsScript) =>
        OperatingSystem.IsWindows()
            ? ["powershell", "-NoProfile", "-NonInteractive", "-Command", windowsScript]
            : ["sh", "-c", unixScript];

    [Fact]
    public async Task Captures_stdout_and_exit_code()
    {
        var outcome = await _runner.RunAsync(new ProcessSpec { Args = ["git", "--version"], WorkingDirectory = Cwd }, default);

        Assert.True(outcome.Succeeded);
        Assert.Contains("git version", outcome.Stdout, StringComparison.Ordinal);
        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task Reports_nonzero_exit_code()
    {
        var outcome = await _runner.RunAsync(
            new ProcessSpec { Args = Shell("exit 3", "exit 3"), WorkingDirectory = Cwd }, default);

        Assert.Equal(3, outcome.ExitCode);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task Missing_executable_throws_typed_exception()
    {
        var ex = await Assert.ThrowsAsync<ExecutableNotFoundException>(() =>
            _runner.RunAsync(new ProcessSpec { Args = ["taskrail-no-such-tool-xyz"], WorkingDirectory = Cwd }, default));

        Assert.Equal("taskrail-no-such-tool-xyz", ex.Command);
    }

    [Fact]
    public async Task Timeout_kills_process_tree_and_returns_promptly()
    {
        // The parent starts a long-lived child that inherits stdout. If only the parent were killed,
        // the child would hold the pipe open and draining would wait for the grace period.
        var args = Shell(
            "sleep 60 & sleep 60",
            "Start-Process -NoNewWindow powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 60'; Start-Sleep 60");

        var outcome = await _runner.RunAsync(
            new ProcessSpec { Args = args, WorkingDirectory = Cwd, Timeout = TimeSpan.FromSeconds(2) }, default);

        Assert.True(outcome.TimedOut);
        Assert.Equal(-1, outcome.ExitCode);
        Assert.True(outcome.Duration < TimeSpan.FromSeconds(6.5), $"took {outcome.Duration}");
    }

    [Fact]
    public async Task External_cancellation_throws_and_kills()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runner.RunAsync(new ProcessSpec { Args = Shell("sleep 60", "Start-Sleep 60"), WorkingDirectory = Cwd }, cts.Token));
    }

    [Fact]
    public async Task Replaced_environment_does_not_leak_parent_variables()
    {
        const string secretVar = "TASKRAIL_TEST_SECRET";
        Environment.SetEnvironmentVariable(secretVar, "leaked");
        try
        {
            var env = new Dictionary<string, string>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH")!,
                ["TASKRAIL_TEST_ALLOWED"] = "visible",
            };
            if (OperatingSystem.IsWindows())
            {
                env["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!;
            }

            var outcome = await _runner.RunAsync(new ProcessSpec
            {
                Args = Shell(
                    "echo \"[$TASKRAIL_TEST_SECRET][$TASKRAIL_TEST_ALLOWED]\"",
                    "Write-Output \"[$env:TASKRAIL_TEST_SECRET][$env:TASKRAIL_TEST_ALLOWED]\""),
                WorkingDirectory = Cwd,
                Environment = env,
                ReplaceEnvironment = true,
            }, default);

            Assert.Contains("[][visible]", outcome.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretVar, null);
        }
    }

    [Fact]
    public async Task Capture_keeps_tail_and_flags_truncation()
    {
        var args = Shell(
            "i=0; while [ $i -lt 2000 ]; do echo line$i; i=$((i+1)); done",
            "0..1999 | ForEach-Object { \"line$_\" }");

        var outcome = await _runner.RunAsync(
            new ProcessSpec { Args = args, WorkingDirectory = Cwd, MaxCaptureChars = 100 }, default);

        Assert.True(outcome.Truncated);
        Assert.True(outcome.Stdout.Length <= 100);
        Assert.Contains("line1999", outcome.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_full_output_to_file_when_requested()
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "out", "stdout.log");

        await _runner.RunAsync(new ProcessSpec { Args = ["git", "--version"], WorkingDirectory = Cwd, StdoutFile = file }, default);

        Assert.Contains("git version", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }
}
