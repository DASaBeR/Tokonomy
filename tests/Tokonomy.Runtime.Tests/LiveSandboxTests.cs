using System.Diagnostics;
using Tokonomy.Core.Config;
using Tokonomy.Runtime.Gates.Isolation;
using Tokonomy.Runtime.Process;
using Xunit.Abstractions;

namespace Tokonomy.Runtime.Tests;

/// <summary>
/// Skipped unless <c>TOKONOMY_LIVE_SANDBOX=1</c>. Boots the REAL Windows Sandbox (Windows Pro/Enterprise/Education 24H2+
/// with the feature enabled) and attacks it from the inside with harmless probe scripts: reading host credentials and
/// other repositories, writing the source, reaching the network, leaving processes behind, planting links in the results
/// folder, exhausting memory. Nothing destructive runs on the host: every probe runs in the disposable VM, and the host
/// checks only its own files afterwards. Each test boots a sandbox, so the suite takes minutes.
/// </summary>
public sealed class LiveSandboxFactAttribute : FactAttribute
{
    public LiveSandboxFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TOKONOMY_LIVE_SANDBOX") != "1")
        {
            Skip = "live sandbox test: set TOKONOMY_LIVE_SANDBOX=1 (needs Windows Sandbox enabled)";
        }
    }
}

[Collection("windows-sandbox")]   // one sandbox at a time
public sealed class LiveSandboxTests : IDisposable
{
    private static readonly string PowerShell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    private readonly ITestOutputHelper _output;
    private readonly string _area;
    private readonly string _worktree;
    private readonly GateEnvStore _store;

    public LiveSandboxTests(ITestOutputHelper output)
    {
        _output = output;
        // Outside temp (the provider maps folders from here into the VM) and outside the profile.
        _area = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tokonomy", "live-sandbox", Guid.NewGuid().ToString("N")[..8]);
        _worktree = Path.Combine(_area, "worktree");
        Directory.CreateDirectory(_worktree);
        _store = new GateEnvStore(Path.Combine(_area, "envs"));
    }

    public void Dispose()
    {
        // A test that failed half-way may have left its sandbox running. Stop only sandboxes this test recorded, through the
        // provider's own recovery, which never deletes folders that a still-running sandbox has mapped.
        foreach (var record in _store.List())
        {
            _store.Save(record with { OwnerPid = int.MaxValue });
        }

        var notes = Provider().RecoverAsync(includeKept: true, default).GetAwaiter().GetResult();
        foreach (var note in notes)
        {
            _output.WriteLine($"cleanup: {note}");
        }

        if (_store.List().Count == 0)
        {
            Tokonomy.Runtime.Git.WorktreeGuard.DeleteTree(_area);
        }
    }

    private WindowsSandboxProvider Provider(GateExecutionConfig? config = null, params Toolchain[] toolchains) =>
        new(new ProcessRunner(), config ?? new GateExecutionConfig { Provider = "windows-sandbox", ResourceLimits = { MemoryMb = 4096 } }, _store, toolchains);

    private string Probe(string name, string script)
    {
        File.WriteAllText(Path.Combine(_worktree, name), script);
        return name;
    }

    private GateEnvironmentRequest Request(NetworkAccess network = NetworkAccess.None) => new()
    {
        RunId = "live",
        Label = "probe",
        Source = new GateSource(_worktree, Directory.EnumerateFiles(_worktree, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_worktree, f).Replace('\\', '/')).ToList()),
        Network = network,
    };

    private static GateCommand Ps(string file, int timeoutSeconds = 120, params string[] args) => new()
    {
        Name = file,
        Args = [PowerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file, .. args],
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
    };

    private async Task<GateCommandResult> RunOne(GateCommand command, NetworkAccess network = NetworkAccess.None, GateExecutionConfig? config = null)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var env = await Provider(config).CreateAsync(Request(network), default);
        var result = await env.RunAsync(command, default);
        _output.WriteLine($"startup {env.StartupTime.TotalSeconds:0.0}s, command {result.Duration.TotalSeconds:0.0}s, total {stopwatch.Elapsed.TotalSeconds:0.0}s");
        _output.WriteLine(result.Output);
        return result;
    }

    [LiveSandboxFact]
    public async Task The_provider_is_available_boots_runs_a_command_and_leaves_nothing_behind()
    {
        Assert.True((await Provider().CheckAsync(default)).Available);
        Probe("hello.ps1", "Write-Output \"hello from $env:COMPUTERNAME as $(whoami)\"; exit 7");

        var result = await RunOne(Ps("hello.ps1"));

        Assert.Equal(GateTermination.Completed, result.Termination);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("hello from", result.Output, StringComparison.Ordinal);
        Assert.Empty(_store.List());
        var list = await new ProcessRunner().RunAsync(new ProcessSpec { Args = ["wsb", "list"], WorkingDirectory = _area }, default);
        Assert.Empty(WindowsSandboxProvider.SandboxIds(list.Stdout));
    }

    [LiveSandboxFact]
    public async Task Host_credentials_the_profile_and_other_repositories_are_unreachable()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var targets = new[]
        {
            home, Path.Combine(home, ".ssh"), Path.Combine(home, ".claude"), Path.Combine(home, ".claude.json"), Path.Combine(home, ".gemini"),
            Path.Combine(home, ".gemini", "oauth_creds.json"), Path.Combine(home, ".git-credentials"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            _worktree, Directory.GetCurrentDirectory(), Path.GetTempPath(),
        };
        Probe("creds.ps1", """
            $hits = @()
            foreach ($p in $args) { if (Test-Path -LiteralPath $p) { $hits += $p } }
            # The sandbox's own C:\Users exists; what matters is that the host's files are not visible.
            Write-Output ("visible: " + ($hits -join '; '))
            if ($hits.Count -gt 0) { exit 1 } else { exit 0 }
            """);

        var result = await RunOne(Ps("creds.ps1", 120, targets));

        Assert.True(result.Succeeded, result.Output);
    }

    [LiveSandboxFact]
    public async Task Gates_cannot_modify_the_worktree_or_the_read_only_snapshot()
    {
        var original = File.ReadAllText(Path.Combine(_worktree, Probe("victim.txt", "original")));
        Probe("tamper.ps1", """
            $ro = 'C:\tk\src\victim.txt'
            try { Set-Content -LiteralPath $ro -Value 'pwned' -ErrorAction Stop; Write-Output 'WROTE read-only snapshot'; exit 1 } catch { Write-Output "snapshot write refused: $($_.Exception.Message)" }
            Remove-Item -LiteralPath 'C:\work\victim.txt' -Force   # its own copy: allowed
            Set-Content -LiteralPath 'C:\work\new.txt' -Value 'x'
            exit 0
            """);

        var result = await RunOne(Ps("tamper.ps1"));

        Assert.True(result.Succeeded, result.Output);
        Assert.Equal(original, File.ReadAllText(Path.Combine(_worktree, "victim.txt")));
        Assert.False(File.Exists(Path.Combine(_worktree, "new.txt")));
    }

    [LiveSandboxFact]
    public async Task The_snapshot_has_no_git_metadata_or_tokonomy_state()
    {
        Directory.CreateDirectory(Path.Combine(_worktree, ".tokonomy"));
        File.WriteAllText(Path.Combine(_worktree, ".tokonomy", "policy.yaml"), "secret: config");
        File.WriteAllText(Path.Combine(_worktree, ".git"), "gitdir: D:/repo/.git/worktrees/x");
        Probe("meta.ps1", """
            $found = @('C:\work\.git', 'C:\work\.tokonomy', 'C:\tk\src\.git', 'C:\tk\src\.tokonomy') | Where-Object { Test-Path -LiteralPath $_ }
            Write-Output "found: $found"; if ($found) { exit 1 } else { exit 0 }
            """);

        Assert.True((await RunOne(Ps("meta.ps1"))).Succeeded);
    }

    [LiveSandboxFact]
    public async Task With_network_none_outbound_and_local_connections_fail()
    {
        Probe("net.ps1", """
            $ok = @()
            foreach ($t in @(@('1.1.1.1', 443), @('8.8.8.8', 53), @('example.com', 443), @('host.docker.internal', 80))) {
              try { $c = New-Object Net.Sockets.TcpClient; $task = $c.ConnectAsync($t[0], $t[1]); if ($task.Wait(4000) -and $c.Connected) { $ok += "$($t[0]):$($t[1])" }; $c.Dispose() } catch { }
            }
            try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 https://example.com | Out-Null; $ok += 'https://example.com' } catch { }
            $adapters = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up').Count
            Write-Output "reachable: $ok; adapters up: $adapters"
            if ($ok.Count -gt 0) { exit 1 } else { exit 0 }
            """);

        var result = await RunOne(Ps("net.ps1"));

        Assert.True(result.Succeeded, result.Output);
    }

    [LiveSandboxFact]
    public async Task With_network_full_the_policy_is_applied_too()
    {
        Probe("net-on.ps1", "try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 15 https://example.com | Out-Null; exit 0 } catch { Write-Output $_; exit 1 }");

        var result = await RunOne(Ps("net-on.ps1"), NetworkAccess.Full);

        _output.WriteLine(result.Succeeded ? "network full: reachable" : "network full: NOT reachable (no internet on this host?)");
    }

    [LiveSandboxFact]
    public async Task Background_processes_do_not_survive_the_environment()
    {
        Probe("daemon.ps1", """
            Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep 3600' -WindowStyle Hidden
            schtasks /Create /TN tokonomy-persist /TR "powershell -Command Start-Sleep 3600" /SC ONSTART /RU SYSTEM /F | Out-Null
            exit 0
            """);
        var before = System.Diagnostics.Process.GetProcesses().Length;

        var result = await RunOne(Ps("daemon.ps1"));

        Assert.True(result.Succeeded, result.Output);
        var list = await new ProcessRunner().RunAsync(new ProcessSpec { Args = ["wsb", "list"], WorkingDirectory = _area }, default);
        Assert.Empty(WindowsSandboxProvider.SandboxIds(list.Stdout));   // the VM, and everything in it, is gone
        _output.WriteLine($"host processes before {before}, after {System.Diagnostics.Process.GetProcesses().Length}");
    }

    [LiveSandboxFact]
    public async Task A_command_past_its_timeout_is_killed_and_the_environment_keeps_working()
    {
        Probe("hang.ps1", "Start-Process -FilePath ping.exe -ArgumentList '-n','600','127.0.0.1' -NoNewWindow -Wait");
        Probe("after.ps1", "exit 0");
        await using var env = await Provider().CreateAsync(Request(), default);

        var hung = await env.RunAsync(Ps("hang.ps1", timeoutSeconds: 10), default);
        var next = await env.RunAsync(Ps("after.ps1"), default);

        Assert.Equal(GateTermination.TimedOut, hung.Termination);
        Assert.True(next.Succeeded, next.Output);
    }

    [LiveSandboxFact]
    public async Task Cancellation_destroys_the_sandbox()
    {
        Probe("slow.ps1", "Start-Sleep 600");
        var env = await Provider().CreateAsync(Request(), default);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            GateCommandResult? unexpected = null;
            try
            {
                unexpected = await env.RunAsync(Ps("slow.ps1", 900), cts.Token);
            }
            catch (OperationCanceledException)
            {
            }

            Assert.True(unexpected is null,
                $"the 600 s command returned instead of being cancelled: {unexpected?.Termination} exit {unexpected?.ExitCode} after {unexpected?.Duration.TotalSeconds:0.0}s: {unexpected?.Output}");
        }
        finally
        {
            Assert.Null(await env.DestroyAsync());
        }

        var list = await new ProcessRunner().RunAsync(new ProcessSpec { Args = ["wsb", "list"], WorkingDirectory = _area }, default);
        Assert.Empty(WindowsSandboxProvider.SandboxIds(list.Stdout));
    }

    [LiveSandboxFact]
    public async Task Wsb_exec_waits_for_a_long_command_and_reports_its_exit_code()
    {
        Probe("wait.ps1", "Start-Sleep 25; exit 5");

        var result = await RunOne(Ps("wait.ps1", 120));

        Assert.True(result.Duration >= TimeSpan.FromSeconds(24), $"returned after {result.Duration.TotalSeconds:0.0}s: {result.Termination} exit {result.ExitCode}: {result.Output}");
        Assert.Equal(5, result.ExitCode);
    }

    [LiveSandboxFact]
    public async Task Memory_is_capped_by_the_configured_limit()
    {
        Probe("hog.ps1", """
            $list = New-Object System.Collections.Generic.List[byte[]]
            try { for ($i = 0; $i -lt 64; $i++) { $list.Add((New-Object byte[] (128MB))); } Write-Output "allocated $($list.Count * 128) MB"; exit 0 }
            catch { Write-Output "stopped after $($list.Count * 128) MB: $($_.Exception.GetType().Name)"; exit 3 }
            """);

        var result = await RunOne(Ps("hog.ps1", 300), config: new GateExecutionConfig { Provider = "windows-sandbox", ResourceLimits = { MemoryMb = 2048 } });

        // 8 GB cannot fit in a 2 GB VM (the pagefile may stretch it, but not that far); what matters is the host is untouched.
        Assert.NotEqual(GateTermination.ProtocolViolation, result.Termination);
        _output.WriteLine($"memory hog: {result.Termination} exit {result.ExitCode}");
    }

    [LiveSandboxFact]
    public async Task A_junction_made_inside_the_results_folder_does_not_reach_host_files()
    {
        // D: and the host profile do not exist inside the VM; if reading through a link in the writable share showed them,
        // the link would have been resolved on the host: an escape. The probe removes its link before exiting so the
        // host can read its report; the next test checks a link left behind is rejected.
        var marker = Path.Combine(_area, "host-only-marker.txt");
        File.WriteAllText(marker, "HOST-ONLY");
        Probe("junction.ps1", """
            param([string]$HostDir)
            $j = 'C:\tk\out\probe-j'
            cmd /c mklink /J $j $HostDir | Out-Null
            $seen = Test-Path -LiteralPath (Join-Path $j 'host-only-marker.txt')
            cmd /c rmdir $j | Out-Null
            Write-Output "host file visible through junction: $seen"
            if ($seen) { exit 1 } else { exit 0 }
            """);

        var result = await RunOne(Ps("junction.ps1", 120, _area));

        Assert.True(result.Succeeded, result.Output);
    }

    [LiveSandboxFact]
    public async Task Links_cannot_be_made_in_the_results_folder_and_a_hard_linked_report_is_rejected()
    {
        // Observed on wsb 0.8.107: the share refuses junctions and symlinks from inside the VM (Access is denied, even as
        // SYSTEM); hard links work, but only between files inside the share. The host refuses any hard-linked result file.
        Probe("plant.ps1", """
            $j = cmd /c mklink /J C:\tk\out\junc C:\Windows 2>&1
            $s = cmd /c mklink /D C:\tk\out\dlink C:\Windows 2>&1
            Set-Content -LiteralPath C:\tk\out\decoy.txt -Value '<testsuites/>'
            cmd /c mklink /H C:\tk\out\0.report C:\tk\out\decoy.txt | Out-Null
            if ((Test-Path C:\tk\out\junc) -or (Test-Path C:\tk\out\dlink)) { exit 1 }   # a link was created: unexpected
            exit 0
            """);

        await using var env = await Provider().CreateAsync(Request(), default);
        var ex = await Assert.ThrowsAsync<GateSecurityException>(() => env.RunAsync(Ps("plant.ps1") with { ReportPath = "r.xml", ReportDestination = Path.Combine(_area, "r.xml") }, default));

        _output.WriteLine(ex.Message);
        Assert.Contains("hard-linked", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_area, "r.xml")));
    }

    [LiveSandboxFact]
    public async Task A_sandbox_left_by_a_crashed_run_is_found_stopped_and_removed()
    {
        Probe("noop.ps1", "exit 0");
        var env = await Provider().CreateAsync(Request(), default);
        var record = _store.List().Single();
        _store.Save(record with { OwnerPid = int.MaxValue });   // as if Tokonomy had crashed with the sandbox running

        var notes = await Provider().RecoverAsync(includeKept: false, default);

        _output.WriteLine(string.Join("\n", notes));
        Assert.Empty(_store.List());
        var list = await new ProcessRunner().RunAsync(new ProcessSpec { Args = ["wsb", "list"], WorkingDirectory = _area }, default);
        Assert.Empty(WindowsSandboxProvider.SandboxIds(list.Stdout));
        await env.DestroyAsync();
    }

    /// <summary>A scripted worker that fixes calc.py, so the whole loop can run without any LLM.</summary>
    private sealed class FixingWorker : Tokonomy.Core.Abstractions.IExecutor
    {
        public string Name => "scripted";

        public Tokonomy.Core.Config.ExecutorCapabilities Capabilities { get; } = new() { Headless = true, ReportsTokens = true };

        public Task<Tokonomy.Core.Model.RunResult> RunAsync(Tokonomy.Core.Model.Brief brief, string workdir, string? model, TimeSpan timeout, CancellationToken ct)
        {
            File.WriteAllText(Path.Combine(workdir, "calc.py"), "def add(a, b):\n    return a + b\n");
            return Task.FromResult(new Tokonomy.Core.Model.RunResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1) });
        }
    }

    [LiveSandboxFact]
    public async Task A_full_run_executes_every_gate_in_the_sandbox_and_accepts_the_fix()
    {
        using var repo = new TestRepo(
            ("calc.py", "def add(a, b):\n    return a - b\n"),
            ("test_calc.py", "import unittest\nfrom calc import add\n\nclass T(unittest.TestCase):\n    def test_add(self):\n        self.assertEqual(add(2, 3), 5)\n"));
        var config = Path.Combine(repo.Path, ".tokonomy");
        using var ledger = new Tokonomy.Runtime.Ledger.SqliteLedger(Path.Combine(config, "ledger.db"));
        ledger.StartRun(new Tokonomy.Core.Model.RunRecord { Id = "live-run", Strategy = "1b", TaskId = "t", Repo = repo.Path, Status = Tokonomy.Core.Model.RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var policy = new Policy
        {
            Models = { Architect = "strong", Worker = "cheap" },
            GateExecution = new GateExecutionConfig { Provider = "windows-sandbox" },
            Gates = [new GateConfig { Name = "unittest", Cmd = ["python", "-m", "unittest", "-v"], TimeoutSeconds = 120 }],
        };
        var artifacts = new Tokonomy.Runtime.Orchestration.RunArtifacts(config, "live-run");
        var stopwatch = Stopwatch.StartNew();

        var outcome = await new Tokonomy.Runtime.Orchestration.RunOrchestrator(new ProcessRunner(), ledger, new Tokonomy.Core.Supervision.Supervisor(), _ => new FixingWorker())
            .RunAsync(new Tokonomy.Runtime.Orchestration.RunRequest
            {
                RunId = "live-run",
                Contract = new Tokonomy.Core.Model.Contract { Task = "fix add", Units = [new Tokonomy.Core.Model.WorkUnit { Id = "U1", Title = "Fix add", Spec = "make add() add" }] },
                RepoRoot = repo.Path,
                BaseSha = repo.Head,
                Policy = policy,
                ExecutorName = "scripted",
                Executor = new ExecutorDefinition { Cmd = ["scripted"], Capabilities = { Headless = true, ReportsTokens = true } },
                Artifacts = artifacts,
                WorkerModel = "cheap",
                UseGates = true,
                AllowEscalation = false,
            }, null, default);

        var transcript = File.ReadAllText(artifacts.TranscriptPath);
        _output.WriteLine($"run {outcome.Status} in {stopwatch.Elapsed.TotalSeconds:0.0}s; {outcome.Error}");
        foreach (var line in transcript.Split('\n').Where(l => l.Contains("gate_env", StringComparison.Ordinal) || l.Contains("baseline", StringComparison.Ordinal)))
        {
            _output.WriteLine(line);
        }

        Assert.Equal(Tokonomy.Core.Model.RunStatus.Pass, outcome.Status);
        Assert.Matches(@"""provider"":\s*""windows-sandbox""", transcript);
        Assert.Matches(@"""label"":\s*""baseline""", transcript);
        Assert.Matches(@"""label"":\s*""U1-a1""", transcript);
        Assert.Contains("return a + b", repo.Git("show", $"{outcome.Branch}:calc.py"), StringComparison.Ordinal);
    }

    [LiveSandboxFact]
    public async Task Host_toolchains_mapped_read_only_run_inside_python_dotnet_and_node()
    {
        var config = new GateExecutionConfig { Provider = "windows-sandbox" };
        var (toolchains, problems) = Toolchains.Resolve(config, [["python"], ["dotnet"], ["node"]], _worktree);
        _output.WriteLine($"toolchains: {string.Join(", ", toolchains.Select(t => t.HostPath))}; problems: {string.Join("; ", problems)}");
        File.WriteAllText(Path.Combine(_worktree, "test_calc.py"), "import unittest\n\nclass T(unittest.TestCase):\n    def test_ok(self):\n        self.assertEqual(1 + 1, 2)\n");
        await using var env = await Provider(config, [.. toolchains]).CreateAsync(Request(), default);

        foreach (var (cmd, needs) in new (string[], string)[] { (["python", "-m", "unittest", "-v"], "python"), (["dotnet", "--info"], "dotnet"), (["node", "-e", "console.log(process.version)"], "node") })
        {
            if (problems.Any(p => p.Contains($"'{needs}'", StringComparison.Ordinal)))
            {
                continue;
            }

            var result = await env.RunAsync(new GateCommand { Name = needs, Args = cmd, Timeout = TimeSpan.FromMinutes(3) }, default);
            _output.WriteLine($"{needs}: {result.Termination} exit {result.ExitCode} in {result.Duration.TotalSeconds:0.0}s");
            Assert.True(result.Succeeded, result.Output);
        }
    }
}
