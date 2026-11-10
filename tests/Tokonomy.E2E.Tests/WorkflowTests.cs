using Spectre.Console;
using Tokonomy.Bench;
using Tokonomy.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Ledger;

namespace Tokonomy.E2E.Tests;

/// <summary>Full CLI workflows with a fake agent: init → plan → run → report, and bench with hidden acceptance.</summary>
public sealed class WorkflowTests : IDisposable
{
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"));

    private string ConfigDir => Path.Combine(_repo.Path, ".tokonomy");

    public void Dispose() => _repo.Dispose();

    private static async Task<(int Exit, string Output)> Cli(Services services, params string[] args)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
            Interactive = InteractionSupport.No,
        });
        console.Profile.Width = 220;
        var exit = await TokonomyApp.RunAsync(args, console, services: services);
        return (exit, writer.ToString());
    }

    private void WriteConfig()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(Path.Combine(ConfigDir, "policy.yaml"), $"""
            models:
              architect: strong-model
              worker: cheap-model
            executors:
              default: fake
            gate_execution:
              provider: host-unsafe
            gates:
              - {"{"} name: has-feature, cmd: {Shell.YamlArray(Shell.FileContains("app.txt", "feature"))} {"}"}
            """);
        File.WriteAllText(Path.Combine(ConfigDir, "prices.yaml"), """
            models:
              strong-model: { input: 2, output: 10 }
              cheap-model: { input: 0.1, output: 0.5 }
            """);
        File.WriteAllText(Path.Combine(ConfigDir, "executors.yaml"), """
            executors:
              fake:
                cmd: ["fake"]
                capabilities: { headless: true, model_override: true, reports_tokens: true }
            """);
    }

    [Fact]
    public async Task Plan_run_report_with_strategy_1b()
    {
        WriteConfig();
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nope\n"), FakeExecutor.Write("app.txt", "feature!\n"));
        var services = new Services { ExecutorFactory = _ => executor };

        var (planExit, planOut) = await Cli(services, "plan", "Add the feature", "--strategy", "1b", "--config", ConfigDir);
        Assert.True(planExit == 0, planOut);

        var (runExit, runOut) = await Cli(services, "run", "--config", ConfigDir);
        Assert.True(runExit == 0, runOut);
        Assert.Contains("retrying", runOut, StringComparison.Ordinal);
        Assert.Contains("accepted", runOut, StringComparison.Ordinal);
        Assert.Contains("PASS", runOut, StringComparison.Ordinal);
        Assert.Equal("strong-model", executor.Calls[0].Model);

        var (againExit, againOut) = await Cli(services, "run", "--config", ConfigDir);
        Assert.Equal(2, againExit);
        Assert.Contains("No unexecuted plan", againOut, StringComparison.Ordinal);

        var (reportExit, reportOut) = await Cli(services, "report", "--config", ConfigDir);
        Assert.Equal(0, reportExit);
        Assert.Contains("add-the-feature", reportOut, StringComparison.Ordinal);

        var branch = _repo.Git("branch", "--list", "tokonomy/*").Trim().TrimStart('*', ' ');
        Assert.Equal("feature!\n", _repo.Git("show", $"{branch}:app.txt"));
    }

    [Fact]
    public async Task Bench_judges_by_hidden_acceptance_and_reports_cost_per_success()
    {
        WriteConfig();
        var benchDir = Path.Combine(_repo.Path, "..", Path.GetFileName(_repo.Path) + "-bench");
        Directory.CreateDirectory(Path.Combine(benchDir, "hidden"));
        try
        {
            // The hidden check requires "feature" AND a marker file only the acceptance overlay provides.
            File.WriteAllText(Path.Combine(benchDir, "hidden", "check.txt"), "hidden");
            File.WriteAllText(Path.Combine(benchDir, "tasks.yaml"), $"""
                config: {System.Text.Json.JsonSerializer.Serialize(ConfigDir)}
                tasks:
                  - id: add-feature
                    repo: {System.Text.Json.JsonSerializer.Serialize(_repo.Path)}
                    prompt: Add the feature to app.txt
                    acceptance:
                      files: hidden
                      cmd: {Shell.YamlArray(Shell.FileContains("app.txt", "feature"))}
                """);

            // Strategy 2 (single shot) writes the wrong thing; 1b retries with gate feedback and succeeds.
            var services = new Services
            {
                ExecutorFactory = _ => new FakeExecutor(
                    (b, dir) => File.WriteAllText(Path.Combine(dir, "app.txt"), b.Body.Contains("Previous attempt failed", StringComparison.Ordinal) ? "feature\n" : "wrong\n")),
            };

            var (exit, output) = await Cli(services, "bench", Path.Combine(benchDir, "tasks.yaml"), "--strategies", "1b,2", "--repeat", "2");

            Assert.True(exit == 0, output);
            Assert.True(output.Contains("| 1b | 2 | 2 | 100%", StringComparison.Ordinal), output);
            Assert.True(output.Contains("| 2 | 2 | 0 | 0%", StringComparison.Ordinal), output);

            using var ledger = new SqliteLedger(Path.Combine(ConfigDir, "ledger.db"));
            var benchFile = Directory.GetFiles(Path.Combine(ConfigDir, "runs"), "bench-*.md").Single();
            var benchId = Path.GetFileNameWithoutExtension(benchFile);
            var results = ledger.ListBenchResults(benchId);
            Assert.Equal(4, results.Count);
            Assert.All(results.Where(r => r.Strategy == "2"), r => Assert.False(r.Accepted));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(benchDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("--worker", "unpriced-model")]
    [InlineData("--strong", "unpriced-model")]
    public async Task Bench_refuses_models_without_a_price_because_the_spending_cap_would_not_work(string flag, string model)
    {
        WriteConfig();
        var tasksFile = Path.Combine(_repo.Path, "..", Path.GetFileName(_repo.Path) + "-tasks.yaml");
        File.WriteAllText(tasksFile, $"""
            config: {System.Text.Json.JsonSerializer.Serialize(ConfigDir)}
            tasks:
              - id: t1
                repo: {System.Text.Json.JsonSerializer.Serialize(_repo.Path)}
                prompt: change app.txt
                acceptance:
                  cmd: {Shell.YamlArray(Shell.FileContains("app.txt", "feature"))}
            """);
        try
        {
            // Strategy 1b uses the strong model as worker; strategy 2 uses the cheap one: cover both overrides.
            var strategy = flag == "--strong" ? "1b" : "2";
            var (exit, output) = await Cli(new Services(), "bench", tasksFile, "--strategies", strategy, "--repeat", "1", flag, model, "--max-usd", "1");

            Assert.Equal(3, exit);
            Assert.Contains("No price in prices.yaml for: unpriced-model", output.ReplaceLineEndings(" ").Replace("  ", " "), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tasksFile);
        }
    }

    [Fact]
    public void Seed_taskset_in_examples_is_valid()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Tokonomy.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repo root not found");
        }

        var taskset = Taskset.Load(Path.Combine(root, "examples", "tasks.yaml"));

        Assert.Equal(3, taskset.Tasks.Count);
        Assert.True(Directory.Exists(taskset.ConfigDirectory));
        Assert.All(taskset.Tasks, t =>
        {
            Assert.True(Directory.Exists(taskset.Resolve(t.Repo)), t.Repo);
            Assert.True(Directory.Exists(taskset.Resolve(t.Acceptance.Files!)), t.Acceptance.Files);
        });
    }

    [Fact]
    public void Hard_taskset_is_valid_and_every_task_has_a_reference_solution()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Tokonomy.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repo root not found");
        }

        var taskset = Taskset.Load(Path.Combine(root, "examples", "tasks-hard.yaml"));

        Assert.Equal(8, taskset.Tasks.Count);
        Assert.All(taskset.Tasks, t =>
        {
            Assert.Equal("shop-demo", t.Repo);
            Assert.True(Directory.Exists(taskset.Resolve(t.Repo)), t.Repo);
            Assert.True(Directory.Exists(taskset.Resolve(t.Acceptance.Files!)), t.Acceptance.Files);
            Assert.True(Directory.Exists(Path.Combine(root, "examples", "reference", t.Id)), $"no reference solution for {t.Id}");
            Assert.True(t.Prompt.Length > 200, $"{t.Id}: prompt should state the API precisely");
        });
    }

    [Fact]
    public void Report_math_uses_accepted_trials_only()
    {
        BenchResultRecord R(string s, bool ok, decimal usd) => new()
        {
            BenchId = "b", TaskId = "t", Strategy = s, Repeat = 1, RunId = Guid.NewGuid().ToString(), RunStatus = RunStatus.Pass, Accepted = ok, Usd = usd,
        };

        var stats = BenchReport.Summarize([R("1", true, 1.0m), R("1", false, 1.0m), R("4", true, 0.2m), R("4", true, 0.4m), R("2", false, 0.1m)]);

        Assert.Equal(2.0m, stats.Single(s => s.Strategy == "1").CostPerSuccess);
        Assert.Equal(0.3m, stats.Single(s => s.Strategy == "4").CostPerSuccess);
        Assert.Null(stats.Single(s => s.Strategy == "2").CostPerSuccess);
    }
}
