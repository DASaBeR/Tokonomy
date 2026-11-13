using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Tests;

/// <summary>KillAll and the tracked count are process-wide, so these tests must not run beside tests that start processes.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExclusiveChildProcesses
{
    public const string Name = "child-processes (exclusive)";
}

[Collection(ExclusiveChildProcesses.Name)]
public class ChildProcessesTests
{
    private static string[] LongSleep => OperatingSystem.IsWindows()
        ? ["ping", "-n", "60", "127.0.0.1"]
        : ["sleep", "60"];

    [Fact]
    public async Task A_running_child_is_tracked_busy_and_KillAll_ends_it()
    {
        var run = new ProcessRunner().RunAsync(new ProcessSpec { Args = LongSleep, WorkingDirectory = Path.GetTempPath() }, default);

        await WaitUntilAsync(() => ChildProcesses.Count > 0);
        Assert.True(ChildProcesses.Busy > 0);

        ChildProcesses.KillAll();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.NotEqual(0, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
        await WaitUntilAsync(() => ChildProcesses.Count == 0);
    }

    [Fact]
    public async Task Cancelling_kills_the_child_and_leaves_nothing_tracked()
    {
        using var cts = new CancellationTokenSource();
        var run = new ProcessRunner().RunAsync(new ProcessSpec { Args = LongSleep, WorkingDirectory = Path.GetTempPath() }, cts.Token);
        await WaitUntilAsync(() => ChildProcesses.Count > 0);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
        await WaitUntilAsync(() => ChildProcesses.Count == 0);
    }

    [Fact]
    public void BeginWork_counts_until_disposed()
    {
        var before = ChildProcesses.Busy;
        using (ChildProcesses.BeginWork())
        {
            Assert.Equal(before + 1, ChildProcesses.Busy);
        }

        Assert.Equal(before, ChildProcesses.Busy);
    }

    [Fact]
    public async Task On_Windows_a_child_is_put_in_the_kill_on_close_job()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var run = new ProcessRunner().RunAsync(new ProcessSpec { Args = LongSleep, WorkingDirectory = Path.GetTempPath() }, default);
        await WaitUntilAsync(() => ChildProcesses.Count > 0);

        var pid = System.Diagnostics.Process.GetProcessesByName("PING").Where(p => !p.HasExited).OrderByDescending(p => p.StartTime).FirstOrDefault();
        Assert.NotNull(pid);
        Assert.True(ChildProcesses.IsInJob(pid));

        ChildProcesses.KillAll();
        await run.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not reached in time");
            await Task.Delay(50);
        }
    }
}
