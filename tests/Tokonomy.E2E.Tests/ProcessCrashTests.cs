using System.Diagnostics;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;

namespace Tokonomy.E2E.Tests;

/// <summary>
/// A real crash: the `tokonomy run` process is killed (whole process tree, no cleanup) while a worker is busy on the
/// second unit, and `tokonomy run --resume` finishes the run in a new process. The worker is a small script driven by
/// the brief, run through the normal config-driven executor; gates are real commands.
/// </summary>
public sealed class ProcessCrashTests : IDisposable
{
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"), ("README.md", "# demo\n"));
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "tokonomy-crash", Guid.NewGuid().ToString("N"));

    private string Config => Path.Combine(_repo.Path, ".tokonomy");
    private string Log => Path.Combine(_scratch, "worker.log");
    private string Block => Path.Combine(_scratch, "block-u2");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }

        _repo.Dispose();
    }

    private static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s.Replace('\\', '/'));

    private void Setup()
    {
        Directory.CreateDirectory(_scratch);
        Directory.CreateDirectory(Config);
        var script = Path.Combine(_scratch, "worker.py");
        File.WriteAllText(script, """
            import os, pathlib, re, sys, time
            workdir, log, block = sys.argv[1], sys.argv[2], sys.argv[3]
            brief = pathlib.Path(workdir, "AGENTS.md").read_text(encoding="utf-8")
            unit = re.search(r"## Your unit: (\S+)", brief).group(1)
            with open(log, "a") as f:
                f.write(unit + " start\n")
            if unit == "U1":
                pathlib.Path(workdir, "app.txt").write_text("feature v2\n")
            elif os.path.exists(block):
                time.sleep(600)
            else:
                pathlib.Path(workdir, "DOCS.md").write_text("docs\n")
            """);
        File.WriteAllText(Block, "");
        File.WriteAllText(Path.Combine(Config, "policy.yaml"), $$"""
            models: { architect: strong, worker: cheap }
            provider: { base_url: "https://example.invalid/v1", api_key_env: TOKONOMY_TEST_UNSET_KEY_7f3 }
            executors: { default: script }
            gates:
              - { name: has-feature, cmd: ["python", "-c", "import sys; sys.exit(0 if 'feature' in open('app.txt').read() else 1)"] }
            gate_execution: { provider: host-unsafe }
            """);
        File.WriteAllText(Path.Combine(Config, "executors.yaml"), $$"""
            executors:
              script:
                cmd: ["python", {{Q(script)}}, "{workdir}", {{Q(Log)}}, {{Q(Block)}}]
                brief_file: AGENTS.md
                capabilities: { headless: true }
            """);
        File.WriteAllText(Path.Combine(Config, "prices.yaml"), "models: {}\n");

        using var ledger = new SqliteLedger(Path.Combine(Config, "ledger.db"));
        ledger.StartRun(new RunRecord { Id = "run-1", Strategy = "4", TaskId = "t", Repo = _repo.Path, BaseSha = _repo.Head, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        new RunArtifacts(Config, "run-1").SavePlan(new PlanFile
        {
            RunId = "run-1",
            Task = "feature v2 with docs",
            Strategy = "4",
            BaseSha = _repo.Head,
            Created = DateTimeOffset.UtcNow,
            Contract = new Contract
            {
                Task = "feature v2 with docs",
                Units =
                [
                    new WorkUnit { Id = "U1", Title = "Feature", Spec = "app.txt says feature v2" },
                    new WorkUnit { Id = "U2", Title = "Docs", Spec = "write DOCS.md", DependsOn = ["U1"] },
                ],
            },
        });
    }

    private Process Start(params string[] args)
    {
        var cli = typeof(Tokonomy.Cli.TokonomyApp).Assembly.Location;
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _repo.Path };
        foreach (var a in (string[])[cli, .. args, "--config", Config])
        {
            psi.ArgumentList.Add(a);
        }

        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    /// <summary>A killed process can hold the ledger files for a moment on Windows; wait for them instead of failing.</summary>
    private SqliteLedger OpenLedger()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new SqliteLedger(Path.Combine(Config, "ledger.db"));
            }
            catch (Microsoft.Data.Sqlite.SqliteException) when (attempt < 40)
            {
                Thread.Sleep(250);
            }
        }
    }

    private string[] LogLines() => File.Exists(Log) ? File.ReadAllLines(Log) : [];

    [Fact]
    public async Task A_killed_run_is_resumed_in_a_new_process_without_redoing_completed_work()
    {
        Setup();

        // 1. Start the run and kill it while the worker is busy on U2 (U1 is committed by then).
        using (var first = Start("run", "--plan", "run-1"))
        {
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (!LogLines().Contains("U2 start") && DateTime.UtcNow < deadline && !first.HasExited)
            {
                await Task.Delay(200);
            }

            Assert.Contains("U2 start", LogLines());
            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync();
        }

        using (var ledger = OpenLedger())
        {
            Assert.Equal(RunStatus.Running, ledger.GetRun("run-1")!.Status); // no cleanup ran: a real crash
        }

        // 2. Resume in a fresh process.
        File.Delete(Block);
        using var second = Start("run", "--resume", "run-1");
        Assert.True(second.WaitForExit(TimeSpan.FromMinutes(3)), "the resumed run did not finish");

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(1, LogLines().Count(l => l == "U1 start"));   // U1 was not executed again
        Assert.Equal(2, LogLines().Count(l => l == "U2 start"));
        var subjects = _repo.Git("log", "--format=%s", "tokonomy/run-1");
        Assert.Equal(1, subjects.Split('\n').Count(s => s == "tokonomy(U1): Feature"));
        Assert.Contains("tokonomy(U2): Docs", subjects, StringComparison.Ordinal);
        using var after = OpenLedger();
        Assert.Equal(RunStatus.Pass, after.GetRun("run-1")!.Status);
    }
}
