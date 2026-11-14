using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tokonomy.Core.Config;
using Tokonomy.Runtime.Gates;
using Tokonomy.Runtime.Gates.Isolation;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Tests;

/// <summary>A test that needs Windows (Windows PowerShell 5.1, junctions, Windows Sandbox's runner).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}

/// <summary>
/// A scripted stand-in for the <c>wsb</c> CLI. It keeps a list of "running" sandboxes and, for <c>exec</c>, plays the
/// part of the code inside the sandbox by writing into the host folder mapped at <c>C:\tk\out</c>, which is exactly what
/// hostile code inside a real sandbox can do and nothing more.
/// </summary>
internal sealed partial class FakeWsb : IProcessRunner
{
    public List<string> Running { get; } = [];
    public List<IReadOnlyList<string>> Calls { get; } = [];
    public List<string> Configs { get; } = [];
    public bool Missing { get; set; }
    public bool FailStart { get; set; }
    public int ProbeFailures { get; set; }
    public bool HangRuns { get; set; }
    public bool RefuseStop { get; set; }
    public int SetupExitCode { get; set; }

    /// <summary>Plays one gate command inside the sandbox; returns the exit code <c>wsb exec</c> reports.</summary>
    public Func<SandboxSide, int, int>? OnRun { get; set; }

    /// <summary>Plays the dependency export.</summary>
    public Func<SandboxSide, int>? OnExport { get; set; }

    public string? OutDir { get; private set; }
    public string? CtlDir { get; private set; }

    public int Starts => Calls.Count(c => c.Count > 1 && c[1] == "start");
    public int Stops => Calls.Count(c => c.Count > 1 && c[1] == "stop");

    public async Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        Calls.Add(spec.Args);
        if (Missing)
        {
            throw new ExecutableNotFoundException(spec.Args[0]);
        }

        var verb = spec.Args[1];
        switch (verb)
        {
            case "list":
                return Ok(string.Join("\n", Running.Select(id => $"Id: {id}")));
            case "start":
                Configs.Add(spec.Args[3]);
                if (FailStart)
                {
                    return new ProcessOutcome { ExitCode = 1, TimedOut = false, Duration = TimeSpan.Zero, Stdout = "", Stderr = "Windows Sandbox is not enabled" };
                }

                var xml = XElement.Parse(spec.Args[3]);
                foreach (var m in xml.Descendants("MappedFolder"))
                {
                    var sandbox = m.Element("SandboxFolder")!.Value;
                    if (sandbox == @"C:\tk\out") { OutDir = m.Element("HostFolder")!.Value; }
                    if (sandbox == @"C:\tk\ctl") { CtlDir = m.Element("HostFolder")!.Value; }
                }

                var id = Guid.NewGuid().ToString();
                Running.Add(id);
                return Ok($"Windows Sandbox environment started successfully:\nId: {id}");
            case "stop":
                if (!RefuseStop)
                {
                    Running.Remove(spec.Args[3]);
                }

                return Ok("");
            case "exec":
                var command = spec.Args[5];
                if (command.Contains("-Mode probe", StringComparison.Ordinal))
                {
                    return Exit(ProbeFailures-- > 0 ? 1 : 0);
                }

                if (command.Contains("-Mode setup", StringComparison.Ordinal))
                {
                    return Exit(SetupExitCode);
                }

                var side = new SandboxSide(OutDir!, CtlDir!);
                if (command.Contains("-Mode export", StringComparison.Ordinal))
                {
                    return OnExport is null ? Fail(1) : Exit(OnExport(side));
                }

                var index = int.Parse(IndexRegex().Match(command).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                side.Specs[index] = JsonElement.Parse(Convert.FromBase64String(SpecRegex().Match(command).Groups[1].Value));
                if (HangRuns)
                {
                    using var timeout = new CancellationTokenSource(spec.Timeout ?? TimeSpan.FromDays(1));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, linked.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return new ProcessOutcome { ExitCode = -1, TimedOut = true, Duration = spec.Timeout!.Value, Stdout = "", Stderr = "" };
                    }
                }

                return Exit((OnRun ?? ((s, i) => s.Complete(i, 0, "ok")))(side, index));
            default:
                return Fail(2);
        }
    }

    private static ProcessOutcome Ok(string stdout) => new() { ExitCode = 0, TimedOut = false, Duration = TimeSpan.Zero, Stdout = stdout, Stderr = "" };

    private static ProcessOutcome Fail(int code) => new() { ExitCode = code, TimedOut = false, Duration = TimeSpan.Zero, Stdout = "", Stderr = "failed" };

    // Like wsb 0.8.107: `wsb exec` exits 0 and reports the command's exit code only in its (--raw JSON) output.
    private static ProcessOutcome Exit(int code) => Ok($"{{\n  \"ExitCode\": {code}\n}}");

    [GeneratedRegex(@"-Spec ([A-Za-z0-9+/=]+)")]
    private static partial Regex SpecRegex();

    [GeneratedRegex(@"-Index (\d+)")]
    private static partial Regex IndexRegex();
}

/// <summary>What code inside the sandbox can touch: the writable results folder (and it can read the control folder).</summary>
internal sealed record SandboxSide(string Out, string Ctl)
{
    public string Nonce => JsonElement.Parse(File.ReadAllText(Path.Combine(Ctl, "session.json"))).GetProperty("nonce").GetString()!;

    /// <summary>Command specs as the runner receives them (decoded from the exec command line).</summary>
    public Dictionary<int, JsonElement> Specs { get; } = [];

    public JsonElement Command(int index) => Specs[index];

    public int Complete(int index, int exit, string log, string? nonce = null, bool timedOut = false)
    {
        File.WriteAllText(Path.Combine(Out, $"{index}.log"), log);
        WriteStatus(index, exit, nonce, timedOut);
        return exit;
    }

    public void WriteStatus(int index, int exit, string? nonce = null, bool timedOut = false) =>
        File.WriteAllText(Path.Combine(Out, $"{index}.status.json"),
            JsonSerializer.Serialize(new { nonce = nonce ?? Nonce, index, exit, timed_out = timedOut, not_found = false }));
}

public sealed class WindowsSandboxProviderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeWsb _wsb = new();
    private readonly GateExecutionConfig _config = new() { Provider = GateExecutionConfig.WindowsSandboxProvider, StartupTimeoutSeconds = 5 };
    private readonly string _src;
    private readonly GateEnvStore _store;

    public WindowsSandboxProviderTests()
    {
        _src = Path.Combine(_dir.Path, "worktree");
        _dir.File("worktree/app.py", "print('hi')\n");
        _dir.File("worktree/tests/test_app.py", "assert True\n");
        _store = new GateEnvStore(Path.Combine(_dir.Path, "gate-envs"));
    }

    public void Dispose() => _dir.Dispose();

    private WindowsSandboxProvider Provider(params Toolchain[] toolchains) =>
        new(_wsb, _config, _store, toolchains, pollInterval: TimeSpan.FromMilliseconds(10), commandGrace: TimeSpan.FromMilliseconds(50));

    private GateEnvironmentRequest Request(NetworkAccess network = NetworkAccess.None, string? deps = null) => new()
    {
        RunId = "run-1",
        Label = "u1-a1",
        Source = new GateSource(_src, ["app.py", "tests/test_app.py"]),
        Network = network,
        Dependencies = deps,
    };

    private static GateCommand Command(string? report = null, string? reportTo = null, int timeoutSeconds = 60) => new()
    {
        Name = "test",
        Args = ["python", "-m", "pytest", "-q", "a b\"c"],
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        ReportPath = report,
        ReportDestination = reportTo,
    };

    [Fact]
    public async Task The_sandbox_gets_no_network_and_every_mapping_except_the_results_folder_is_read_only()
    {
        var tool = Directory.CreateDirectory(Path.Combine(_dir.Path, "tool")).FullName;
        await using (var env = await Provider(new Toolchain(tool, @"C:\tk\tools\t0")).CreateAsync(Request(), default))
        {
        }

        var xml = XElement.Parse(_wsb.Configs.Single());
        Assert.Equal("Disable", xml.Element("Networking")!.Value);
        foreach (var device in new[] { "vGPU", "AudioInput", "VideoInput", "PrinterRedirection", "ClipboardRedirection" })
        {
            Assert.Equal("Disable", xml.Element(device)!.Value);
        }

        var mappings = xml.Descendants("MappedFolder")
            .Select(m => (Host: m.Element("HostFolder")!.Value, Sandbox: m.Element("SandboxFolder")!.Value, ReadOnly: m.Element("ReadOnly")!.Value == "true"))
            .ToList();
        Assert.Equal([@"C:\tk\out"], mappings.Where(m => !m.ReadOnly).Select(m => m.Sandbox));
        Assert.Contains(mappings, m => m.Host == tool && m.ReadOnly);
        // Never the worktree itself, never the profile.
        Assert.DoesNotContain(mappings, m => string.Equals(m.Host, _src, StringComparison.OrdinalIgnoreCase));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(mappings, m => string.Equals(m.Host.TrimEnd('\\'), home, StringComparison.OrdinalIgnoreCase));
        Assert.All(mappings.Where(m => m.Host != tool), m => Assert.StartsWith(_store.Root, m.Host, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Network_is_only_enabled_when_the_request_says_so()
    {
        await using (await Provider().CreateAsync(Request(NetworkAccess.Full), default))
        {
        }

        Assert.Equal("Default", XElement.Parse(_wsb.Configs.Single()).Element("Networking")!.Value);
    }

    [Fact]
    public async Task A_command_runs_and_its_result_log_and_report_come_back()
    {
        var reportTo = Path.Combine(_dir.Path, "collected.xml");
        _wsb.OnRun = (side, i) =>
        {
            Assert.Equal("python", side.Command(i).GetProperty("exe").GetString());
            Assert.Equal("-m pytest -q \"a b\\\"c\"", side.Command(i).GetProperty("args_line").GetString());
            File.WriteAllText(Path.Combine(side.Out, $"{i}.report"), "<testsuites/>");
            return side.Complete(i, 1, "1 failed");
        };

        await using var env = await Provider().CreateAsync(Request(), default);
        var result = await env.RunAsync(Command("report.xml", reportTo), default);

        Assert.Equal(GateTermination.Completed, result.Termination);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Contains("1 failed", result.Output, StringComparison.Ordinal);
        Assert.Equal(reportTo, result.ReportFile);
        Assert.Equal("<testsuites/>", File.ReadAllText(reportTo));
    }

    [Fact]
    public async Task The_snapshot_holds_the_source_but_never_git_metadata_or_tokonomy_state()
    {
        _dir.File("worktree/.tokonomy/policy.yaml", "x");
        _dir.File("worktree/.git", "gitdir: C:/somewhere");
        string[]? seen = null;
        _wsb.OnRun = (side, i) =>
        {
            var src = Path.Combine(Path.GetDirectoryName(side.Out)!, "src");
            seen = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(src, f).Replace('\\', '/')).Order().ToArray();
            return side.Complete(i, 0, "");
        };

        var request = Request() with { Source = new GateSource(_src, ["app.py", "tests/test_app.py", ".tokonomy/policy.yaml", ".git"]) };
        await using var env = await Provider().CreateAsync(request, default);
        await env.RunAsync(Command(), default);

        Assert.Equal(["app.py", "tests/test_app.py"], seen!);
    }

    [Fact]
    public async Task A_missing_status_is_a_failure_never_a_pass()
    {
        _wsb.OnRun = (side, i) =>
        {
            File.WriteAllText(Path.Combine(side.Out, $"{i}.log"), "all tests passed");
            return 0;   // exit 0 but no status: the protocol was not followed
        };

        await using var env = await Provider().CreateAsync(Request(), default);
        var result = await env.RunAsync(Command(), default);

        Assert.Equal(GateTermination.ProtocolViolation, result.Termination);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_status_with_the_wrong_nonce_or_index_is_rejected()
    {
        _wsb.OnRun = (side, i) => side.Complete(i, 0, "passed", nonce: "forged");
        await using var env = await Provider().CreateAsync(Request(), default);
        Assert.Equal(GateTermination.ProtocolViolation, (await env.RunAsync(Command(), default)).Termination);

        _wsb.OnRun = (side, i) =>
        {
            side.WriteStatus(i + 7, 0);   // a status for another command
            return 0;
        };
        Assert.Equal(GateTermination.ProtocolViolation, (await env.RunAsync(Command(), default)).Termination);
    }

    [Fact]
    public async Task A_claimed_pass_that_the_sandbox_exit_code_contradicts_is_a_failure()
    {
        _wsb.OnRun = (side, i) =>
        {
            side.WriteStatus(i, 0);
            return 3;
        };

        await using var env = await Provider().CreateAsync(Request(), default);
        var result = await env.RunAsync(Command(), default);

        Assert.False(result.Succeeded);
        Assert.Equal(GateTermination.ProtocolViolation, result.Termination);
    }

    [Fact]
    public async Task A_junction_planted_in_the_results_folder_is_removed_and_fails_the_batch_without_touching_its_target()
    {
        var secret = Path.GetFullPath(_dir.File("outside/secret.txt", "TOKEN"));
        _wsb.OnRun = (side, i) =>
        {
            FsLinks.Directory(Path.Combine(side.Out, $"{i}.report"), Path.GetDirectoryName(secret)!);
            return side.Complete(i, 0, "ok");
        };

        await using var env = await Provider().CreateAsync(Request(), default);
        await Assert.ThrowsAsync<GateSecurityException>(() => env.RunAsync(Command("r.xml", Path.Combine(_dir.Path, "r.xml")), default));

        Assert.Equal("TOKEN", File.ReadAllText(secret));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "r.xml")));
    }

    [WindowsFact]
    public async Task A_hard_linked_report_is_refused()
    {
        var secret = Path.GetFullPath(_dir.File("outside/secret.txt", "TOKEN"));
        _wsb.OnRun = (side, i) =>
        {
            FsLinks.HardLink(Path.Combine(side.Out, $"{i}.report"), secret);
            return side.Complete(i, 0, "ok");
        };

        await using var env = await Provider().CreateAsync(Request(), default);
        await Assert.ThrowsAsync<GateSecurityException>(() => env.RunAsync(Command("r.xml", Path.Combine(_dir.Path, "r.xml")), default));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "r.xml")));
    }

    [Fact]
    public async Task An_oversized_report_is_ignored_not_copied()
    {
        _config.ArtifactLimits.MaxReportMb = 1;
        _wsb.OnRun = (side, i) =>
        {
            File.WriteAllBytes(Path.Combine(side.Out, $"{i}.report"), new byte[2 * 1024 * 1024]);
            return side.Complete(i, 0, "ok");
        };

        await using var env = await Provider().CreateAsync(Request(), default);
        var result = await env.RunAsync(Command("r.xml", Path.Combine(_dir.Path, "r.xml")), default);

        Assert.Null(result.ReportFile);
        Assert.Contains("exceeds", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "r.xml")));
    }

    [Fact]
    public async Task A_command_that_overruns_destroys_the_sandbox_and_the_next_command_gets_a_fresh_one()
    {
        _wsb.HangRuns = true;
        await using var env = await Provider().CreateAsync(Request(), default);
        var gate = Command() with { Timeout = TimeSpan.FromMilliseconds(10) };

        var result = await env.RunAsync(gate, default);   // the host-side timeout (command timeout + grace) expires

        Assert.Equal(GateTermination.TimedOut, result.Termination);
        Assert.Empty(_wsb.Running);   // stopped: nothing it started survives
        _wsb.HangRuns = false;
        var next = await env.RunAsync(Command(), default);
        Assert.True(next.Succeeded);
        Assert.Equal(2, _wsb.Starts);
    }

    [Fact]
    public async Task Cancellation_stops_the_sandbox_before_returning()
    {
        _wsb.HangRuns = true;
        await using var env = await Provider().CreateAsync(Request(), default);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.RunAsync(Command(), cts.Token));

        Assert.Empty(_wsb.Running);
    }

    [Fact]
    public async Task A_sandbox_someone_else_started_blocks_the_run_and_is_never_stopped()
    {
        var theirs = Guid.NewGuid().ToString();
        _wsb.Running.Add(theirs);

        var ex = await Assert.ThrowsAsync<GateEnvironmentException>(() => Provider().CreateAsync(Request(), default));

        Assert.Contains("another Windows Sandbox is running", ex.Message, StringComparison.Ordinal);
        Assert.Equal([theirs], _wsb.Running);
        Assert.Equal(0, _wsb.Stops);
        Assert.Empty(_store.List());   // nothing left behind
    }

    [Fact]
    public async Task A_failed_start_leaves_no_folders_and_says_why()
    {
        _wsb.FailStart = true;

        var ex = await Assert.ThrowsAsync<GateEnvironmentException>(() => Provider().CreateAsync(Request(), default));

        Assert.Contains("not enabled", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task A_sandbox_that_never_becomes_ready_is_stopped_and_cleaned_up()
    {
        _config.StartupTimeoutSeconds = 1;
        _wsb.ProbeFailures = int.MaxValue;

        await Assert.ThrowsAsync<GateEnvironmentException>(() => Provider().CreateAsync(Request(), default));

        Assert.Empty(_wsb.Running);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Destroying_stops_the_sandbox_confirms_it_is_gone_and_deletes_the_folders()
    {
        var env = await Provider().CreateAsync(Request(), default);
        Assert.Single(_wsb.Running);
        Assert.Single(_store.List());

        Assert.Null(await env.DestroyAsync());
        Assert.Null(await env.DestroyAsync());   // idempotent

        Assert.Empty(_wsb.Running);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task When_a_sandbox_cannot_be_stopped_its_folders_are_kept_and_the_problem_reported()
    {
        _wsb.RefuseStop = true;
        var env = await Provider().CreateAsync(Request(), default);

        var problem = await env.DestroyAsync();

        Assert.Contains("could not be stopped", problem, StringComparison.Ordinal);
        Assert.Single(_store.List());   // still mapped into a live sandbox: never delete under it
    }

    [Fact]
    public async Task Keep_on_failure_keeps_the_folders_of_a_failed_batch_and_recovery_leaves_them_until_asked()
    {
        _config.CleanupPolicy = GateExecutionConfig.CleanupKeepOnFailure;
        var env = await Provider().CreateAsync(Request(), default);
        Assert.Contains("kept", await env.DestroyAsync(failed: true), StringComparison.Ordinal);
        Assert.Empty(_wsb.Running);   // the sandbox itself is always destroyed

        var record = _store.List().Single();
        _store.Save(record with { OwnerPid = int.MaxValue });   // as if the run had ended
        Assert.Empty(await Provider().RecoverAsync(includeKept: false, default));
        Assert.Single(_store.List());
        await Provider().RecoverAsync(includeKept: true, default);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Recovery_stops_and_removes_what_a_crashed_run_left_and_leaves_live_runs_alone()
    {
        var crashed = _store.Create("old-run", "u1", GateExecutionConfig.WindowsSandboxProvider);
        var orphanSandbox = Guid.NewGuid().ToString();
        _wsb.Running.Add(orphanSandbox);
        _store.Save(crashed with { OwnerPid = int.MaxValue, SandboxId = orphanSandbox });
        FsLinks.Directory(Path.Combine(crashed.Directory, "planted"), _dir.Path);   // a link left in its folder
        var live = _store.Create("this-run", "u1", GateExecutionConfig.WindowsSandboxProvider);

        var notes = await Provider().RecoverAsync(includeKept: false, default);

        Assert.Contains(notes, n => n.Contains("old-run", StringComparison.Ordinal) && n.EndsWith("removed", StringComparison.Ordinal));
        Assert.DoesNotContain(orphanSandbox, _wsb.Running);
        Assert.False(Directory.Exists(crashed.Directory));
        Assert.True(Directory.Exists(live.Directory));
        Assert.True(File.Exists(Path.Combine(_src, "app.py")));   // the link's target survived
    }

    [Fact]
    public async Task Destroying_an_environment_that_recovery_already_removed_is_harmless()
    {
        var env = await Provider().CreateAsync(Request(), default);
        _store.Save(_store.List().Single() with { OwnerPid = int.MaxValue });
        await Provider().RecoverAsync(includeKept: false, default);

        Assert.Null(await env.DestroyAsync());
        Assert.Empty(_wsb.Running);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Prepared_dependencies_come_back_as_one_archive_within_the_size_limit()
    {
        _wsb.OnExport = side =>
        {
            File.WriteAllText(Path.Combine(side.Out, "deps.tar"), "archive");
            return 0;
        };

        string? archive;
        await using (var env = await Provider().CreateAsync(Request(NetworkAccess.Full), default))
        {
            archive = await env.ExportDependenciesAsync(Path.Combine(_dir.Path, "deps", "deps.tar"), default);
        }

        Assert.Equal("archive", File.ReadAllText(archive!));

        await using var gates = await Provider().CreateAsync(Request(deps: archive), default);
        var depsMapping = XElement.Parse(_wsb.Configs[^1]).Descendants("MappedFolder").Single(m => m.Element("SandboxFolder")!.Value == @"C:\tk\deps");
        Assert.Equal("true", depsMapping.Element("ReadOnly")!.Value);
    }

    [Fact]
    public async Task An_oversized_dependency_archive_is_refused()
    {
        _config.ArtifactLimits.MaxDependenciesMb = 1;
        _wsb.OnExport = side =>
        {
            File.WriteAllBytes(Path.Combine(side.Out, "deps.tar"), new byte[2 * 1024 * 1024]);
            return 0;
        };

        await using var env = await Provider().CreateAsync(Request(), default);
        await Assert.ThrowsAsync<GateSecurityException>(() => env.ExportDependenciesAsync(Path.Combine(_dir.Path, "deps.tar"), default));
    }

    [Fact]
    public async Task A_setup_failure_reported_only_in_wsb_output_is_detected()
    {
        _wsb.SetupExitCode = 71;   // wsb exec itself still exits 0

        var ex = await Assert.ThrowsAsync<GateEnvironmentException>(() => Provider().CreateAsync(Request(), default));

        Assert.Contains("71", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_wsb.Running);
    }

    [Theory]
    [InlineData("{\n  \"ExitCode\": 7\n}", 7)]
    [InlineData("{\"ExitCode\":-1073741510}", -1073741510)]
    [InlineData("Process exited with code: 3", 3)]
    [InlineData("", null)]
    [InlineData("Error: sandbox not found", null)]
    public void The_commands_exit_code_is_read_from_wsb_output(string output, int? expected) =>
        Assert.Equal(expected, WindowsSandboxProvider.ParseRemoteExitCode(output));

    [WindowsFact]
    public async Task Without_wsb_the_provider_is_unavailable_with_instructions()
    {
        _wsb.Missing = true;

        var status = await Provider().CheckAsync(default);

        Assert.False(status.Available);
        Assert.Contains("Containers-DisposableClientVM", status.Detail, StringComparison.Ordinal);
        Assert.Contains("host-unsafe", status.Detail, StringComparison.Ordinal);
    }
}

public sealed class SourceSnapshotTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Worktree => Path.Combine(_dir.Path, "wt");

    private string Destination => Path.Combine(_dir.Path, "snap");

    [Fact]
    public void Only_listed_files_are_copied_and_deleted_or_directory_entries_are_skipped()
    {
        _dir.File("wt/a.txt", "a");
        _dir.File("wt/sub/b.txt", "b");
        _dir.File("wt/unlisted.txt", "u");
        Directory.CreateDirectory(Path.Combine(Worktree, "submodule"));

        var result = SourceSnapshot.Copy(new GateSource(Worktree, ["a.txt", "sub/b.txt", "gone.txt", "submodule"]), Destination, 1024 * 1024);

        Assert.Equal(2, result.Files);
        Assert.True(File.Exists(Path.Combine(Destination, "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(Destination, "unlisted.txt")));
    }

    [Fact]
    public void A_file_reached_through_a_junction_fails_the_snapshot()
    {
        var outside = Path.GetFullPath(_dir.File("outside/id_ed25519", "PRIVATE KEY"));
        Directory.CreateDirectory(Worktree);
        FsLinks.Directory(Path.Combine(Worktree, "keys"), Path.GetDirectoryName(outside)!);

        Assert.Throws<GateSecurityException>(() => SourceSnapshot.Copy(new GateSource(Worktree, ["keys/id_ed25519"]), Destination, 1024 * 1024));
        Assert.False(File.Exists(Path.Combine(Destination, "keys", "id_ed25519")));
    }

    [WindowsFact]
    public void A_hard_link_to_a_file_outside_fails_the_snapshot()
    {
        var outside = Path.GetFullPath(_dir.File("outside/credentials.json", "{\"token\":\"x\"}"));
        Directory.CreateDirectory(Worktree);
        FsLinks.HardLink(Path.Combine(Worktree, "creds.json"), outside);

        Assert.Throws<GateSecurityException>(() => SourceSnapshot.Copy(new GateSource(Worktree, ["creds.json"]), Destination, 1024 * 1024));
    }

    [Fact]
    public void Traversal_in_a_listed_path_is_refused()
    {
        _dir.File("secret.txt", "s");
        Directory.CreateDirectory(Worktree);

        Assert.Throws<GateSecurityException>(() => SourceSnapshot.Copy(new GateSource(Worktree, ["../secret.txt"]), Destination, 1024 * 1024));
    }

    [Fact]
    public void A_snapshot_larger_than_the_limit_is_refused()
    {
        File.WriteAllBytes(_dir.File("wt/big.bin", ""), new byte[64 * 1024]);

        Assert.Throws<GateSecurityException>(() => SourceSnapshot.Copy(new GateSource(Worktree, ["big.bin"]), Destination, 1024));
    }

    [Theory]
    [InlineData(".git", true)]
    [InlineData(".git/hooks/pre-commit", true)]
    [InlineData(".GIT/config", true)]
    [InlineData(".tokonomy/policy.yaml", true)]
    [InlineData(".gitignore", false)]
    [InlineData("src/.git", false)]
    public void Git_metadata_and_tokonomy_state_are_always_excluded(string path, bool excluded) =>
        Assert.Equal(excluded, SourceSnapshot.IsExcluded(path));
}

public sealed class GateExecutionConfigTests
{
    [Fact]
    public void A_missing_section_is_an_error_with_both_choices_spelled_out()
    {
        var ex = Assert.Throws<GateIsolationUnavailableException>(() =>
            GateExecutionFactory.Create(null, new ProcessRunner(), Directory.GetCurrentDirectory(), [], []));

        Assert.Contains("windows-sandbox", ex.Message, StringComparison.Ordinal);
        Assert.Contains("host-unsafe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_provider_is_an_error_not_a_fallback() =>
        Assert.Throws<GateIsolationUnavailableException>(() =>
            GateExecutionFactory.Create(new GateExecutionConfig { Provider = "docker" }, new ProcessRunner(), Directory.GetCurrentDirectory(), [], []));

    [Fact]
    public void Host_unsafe_is_only_what_it_says()
    {
        var provider = GateExecutionFactory.Create(new GateExecutionConfig { Provider = "host-unsafe" }, new ProcessRunner(), Directory.GetCurrentDirectory(), [], []);

        Assert.False(provider.IsIsolated);
    }

    [Fact]
    public void A_toolchain_inside_the_user_profile_is_refused()
    {
        using var tmp = new TempDir();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var problem = Toolchains.Validate(home, tmp.Path, []);

        Assert.NotNull(problem);
        Assert.Contains("profile", problem, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Temp_the_repository_and_drive_roots_are_refused_as_toolchains()
    {
        using var repo = new TempDir();
        Assert.NotNull(Toolchains.Validate(Path.GetTempPath().TrimEnd('\\'), "C:/nowhere", []));
        Assert.NotNull(Toolchains.Validate(repo.Path, repo.Path, []));
        Assert.NotNull(Toolchains.Validate(Path.GetPathRoot(Environment.SystemDirectory)!, repo.Path, []));
        Assert.NotNull(Toolchains.Validate(Environment.GetFolderPath(Environment.SpecialFolder.Windows), repo.Path, []));
    }

    [Fact]
    public void A_program_files_toolchain_is_accepted()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        if (!Directory.Exists(dotnet))
        {
            return;   // no machine-wide .NET install to check against
        }

        Assert.Null(Toolchains.Validate(dotnet, "C:/nowhere", []));
    }

    [WindowsFact]
    public void Toolchains_are_derived_from_the_gate_commands_and_system_tools_need_no_mapping()
    {
        var (mapped, problems) = Toolchains.Resolve(new GateExecutionConfig(), [["cmd", "/c", "exit"], [".venv/Scripts/python", "-m", "pytest"]], "C:/nowhere");

        Assert.Empty(problems);
        Assert.Empty(mapped);   // cmd lives in Windows (the sandbox has its own); a relative command comes from the working copy
    }

    [Fact]
    public void A_gate_tool_that_does_not_exist_is_a_diagnostic_not_a_silent_skip()
    {
        var (_, problems) = Toolchains.Resolve(new GateExecutionConfig(), [["tokonomy-no-such-tool"]], "C:/nowhere");

        Assert.Single(problems);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("quote\"inside", "\"quote\\\"inside\"")]
    [InlineData(@"trailing\", @"trailing\")]
    [InlineData(@"spaced trailing\", "\"spaced trailing\\\\\"")]
    public void Command_lines_quote_like_the_msvc_runtime_parses(string arg, string expected) =>
        Assert.Equal(expected, WindowsCommandLine.Quote(arg));
}

/// <summary>
/// Runs the real in-sandbox runner script with Windows PowerShell 5.1 on the host, against a throwaway folder laid out
/// like the sandbox (the script takes its root as a parameter). The commands are harmless fixtures. This checks the
/// script's own logic (environment, quoting, timeouts, status, export) without needing Windows Sandbox.
/// </summary>
public sealed class SandboxRunnerScriptTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _root;

    public SandboxRunnerScriptTests()
    {
        _root = Path.Combine(_dir.Path, "sbx") + "\\";
        foreach (var d in new[] { "tk/ctl", "tk/out", "tk/src", "tk/deps" })
        {
            Directory.CreateDirectory(Path.Combine(_root, d));
        }

        File.WriteAllText(Path.Combine(_root, "tk", "ctl", "run.ps1"), SandboxRunnerScript.Text);
        File.WriteAllText(Path.Combine(_root, "tk", "ctl", "session.json"), JsonSerializer.Serialize(new { nonce = "n1", toolchains = Array.Empty<string>() }));
    }

    public void Dispose() => _dir.Dispose();

    private async Task<ProcessOutcome> Script(string mode, int index = -1, IReadOnlyDictionary<string, string>? env = null)
    {
        List<string> args = ["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(_root, "tk", "ctl", "run.ps1"), "-Mode", mode, "-Root", _root];
        if (index >= 0)
        {
            args.AddRange(["-Index", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (_specs.TryGetValue(index, out var spec))
        {
            args.AddRange(["-Spec", spec]);
        }

        return await new ProcessRunner().RunAsync(new ProcessSpec { Args = args, WorkingDirectory = _dir.Path, Timeout = TimeSpan.FromMinutes(2), Environment = env }, default);
    }

    private readonly Dictionary<int, string> _specs = [];

    /// <summary>Encodes the command exactly as the provider does; <see cref="Script"/> passes it with -Spec.</summary>
    private void WriteCommand(int index, IReadOnlyList<string> args, int timeoutMs = 60_000, string? report = null) =>
        _specs[index] = SandboxRunnerScript.EncodeSpec(
            new GateCommand { Name = "t", Args = args, Timeout = TimeSpan.FromMilliseconds(timeoutMs), ReportPath = report },
            new GateArtifactLimits { MaxLogKb = 4, MaxReportMb = 1 });

    private JsonElement Status(int index) => JsonElement.Parse(File.ReadAllText(Path.Combine(_root, "tk", "out", $"{index}.status.json")));

    private string Out(string name) => File.ReadAllText(Path.Combine(_root, "tk", "out", name));

    [WindowsFact]
    public async Task Setup_copies_the_snapshot_and_a_command_runs_with_its_exit_code_output_and_report()
    {
        File.WriteAllText(Path.Combine(_root, "tk", "src", "app.txt"), "feature");
        Assert.Equal(0, (await Script("setup")).ExitCode);
        Assert.True(File.Exists(Path.Combine(_root, "work", "app.txt")));

        WriteCommand(0, ["cmd", "/d", "/c", "type app.txt & echo ^<x/^> > report.xml & exit /b 3"], report: "report.xml");
        var run = await Script("run", 0);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal(3, Status(0).GetProperty("exit").GetInt32());
        Assert.Equal("n1", Status(0).GetProperty("nonce").GetString());
        Assert.False(Status(0).GetProperty("timed_out").GetBoolean());
        Assert.Contains("feature", Out("0.log"), StringComparison.Ordinal);
        Assert.Contains("<x/>", Out("0.report"), StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task Arguments_with_spaces_and_quotes_arrive_intact()
    {
        File.WriteAllText(Path.Combine(_root, "tk", "src", "args.ps1"), "$args | ForEach-Object { '<' + $_ + '>' }");
        Assert.Equal(0, (await Script("setup")).ExitCode);
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        WriteCommand(1, [ps, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "args.ps1", "two words", "q\"uote", @"back\slash\"]);

        await Script("run", 1);

        var log = Out("1.log");
        Assert.Contains("<two words>", log, StringComparison.Ordinal);
        Assert.Contains("<q\"uote>", log, StringComparison.Ordinal);
        Assert.Contains(@"<back\slash\>", log, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task A_command_past_its_timeout_is_killed_with_its_children_and_reported_as_timed_out()
    {
        Assert.Equal(0, (await Script("setup")).ExitCode);
        WriteCommand(2, ["cmd", "/d", "/c", "ping -n 60 127.0.0.1 > nul"], timeoutMs: 1500);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var run = await Script("run", 2);

        Assert.Equal(124, run.ExitCode);
        Assert.True(Status(2).GetProperty("timed_out").GetBoolean());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [WindowsFact]
    public async Task A_command_that_does_not_exist_is_reported_as_not_found()
    {
        Assert.Equal(0, (await Script("setup")).ExitCode);
        WriteCommand(3, ["tokonomy-no-such-tool", "x"]);

        await Script("run", 3);

        Assert.True(Status(3).GetProperty("not_found").GetBoolean());
    }

    [WindowsFact]
    public async Task Export_archives_only_what_prepare_added_plus_caches_and_setup_restores_it()
    {
        File.WriteAllText(Path.Combine(_root, "tk", "src", "package.json"), "{}");
        Assert.Equal(0, (await Script("setup")).ExitCode);
        Directory.CreateDirectory(Path.Combine(_root, "work", "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(_root, "work", "node_modules", "left-pad", "index.js"), "module.exports=1");
        Directory.CreateDirectory(Path.Combine(_root, "tkc", "nuget"));
        File.WriteAllText(Path.Combine(_root, "tkc", "nuget", "pkg.txt"), "p");

        Assert.Equal(0, (await Script("export")).ExitCode);
        var archive = Path.Combine(_root, "tk", "out", "deps.tar");
        var listing = (await new ProcessRunner().RunAsync(new ProcessSpec { Args = [Path.Combine(Environment.SystemDirectory, "tar.exe"), "-tf", archive], WorkingDirectory = _dir.Path }, default)).Stdout;
        Assert.Contains("work/node_modules/left-pad/index.js", listing, StringComparison.Ordinal);
        Assert.Contains("tkc/nuget/pkg.txt", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("package.json", listing, StringComparison.Ordinal);

        // A fresh "sandbox" gets the archive read-only and unpacks it before the source.
        Directory.Delete(Path.Combine(_root, "work"), recursive: true);
        Directory.Delete(Path.Combine(_root, "tkc"), recursive: true);
        File.Move(archive, Path.Combine(_root, "tk", "deps", "deps.tar"));
        Assert.Equal(0, (await Script("setup")).ExitCode);
        Assert.True(File.Exists(Path.Combine(_root, "work", "node_modules", "left-pad", "index.js")));
        Assert.True(File.Exists(Path.Combine(_root, "work", "package.json")));
        Assert.True(File.Exists(Path.Combine(_root, "tkc", "nuget", "pkg.txt")));
    }

    [WindowsFact]
    public async Task Variables_inherited_by_the_runner_never_reach_the_command()
    {
        Assert.Equal(0, (await Script("setup")).ExitCode);
        WriteCommand(4, ["cmd", "/d", "/c", "set"]);

        // Inside the sandbox nothing from the host exists; this checks the runner scrubs whatever it inherits anyway.
        await Script("run", 4, new Dictionary<string, string> { ["GH_TOKEN"] = "ghp_leak", ["SSH_AUTH_SOCK"] = "/tmp/agent", ["DATABASE_URL"] = "postgres://u:p@h/db" });

        var env = Out("4.log");
        Assert.DoesNotContain("ghp_leak", env, StringComparison.Ordinal);
        Assert.DoesNotContain("SSH_AUTH_SOCK", env, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres://", env, StringComparison.Ordinal);
        Assert.Contains(@"USERPROFILE=" + Path.Combine(_root, "tkh").TrimEnd('\\'), env, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CI=true", env, StringComparison.Ordinal);
    }
}
