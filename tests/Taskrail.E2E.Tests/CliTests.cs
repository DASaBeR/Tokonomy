using System.Text.Json;
using Spectre.Console;
using Taskrail.Cli;
using Taskrail.Core.Model;
using Taskrail.Runtime.Ledger;

namespace Taskrail.E2E.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "taskrail-e2e", Guid.NewGuid().ToString("N"));

    public CliTests()
    {
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_repo, "pyproject.toml"), "");
    }

    private string ConfigDir => Path.Combine(_repo, ".taskrail");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<(int Exit, string Output)> Run(params string[] args)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
            Interactive = InteractionSupport.No,
        });
        console.Profile.Width = 200;
        var exit = await TaskrailApp.RunAsync(args, console);
        return (exit, writer.ToString());
    }

    [Fact]
    public async Task Init_scaffolds_config_for_detected_stack()
    {
        var (exit, output) = await Run("init", "--path", _repo);

        Assert.Equal(0, exit);
        Assert.Contains("python", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(ConfigDir, "policy.yaml")));
        Assert.True(File.Exists(Path.Combine(ConfigDir, "executors.yaml")));
        Assert.True(File.Exists(Path.Combine(ConfigDir, "prices.yaml")));
        Assert.Contains("ledger.db", File.ReadAllText(Path.Combine(ConfigDir, ".gitignore")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_json_output_is_parseable()
    {
        var (exit, output) = await Run("init", "--path", _repo, "--json");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("python", doc.RootElement.GetProperty("stacks")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Report_lists_runs_from_ledger()
    {
        await Run("init", "--path", _repo);
        using (var ledger = new SqliteLedger(Path.Combine(ConfigDir, "ledger.db")))
        {
            ledger.StartRun(new RunRecord { Id = "run-1", Strategy = "1", TaskId = "demo-task", Repo = _repo, Started = DateTimeOffset.UtcNow });
            ledger.RecordLlmCall(new LlmCallRecord { RunId = "run-1", Role = LlmRole.Worker, Model = "m", InputTokens = 10, OutputTokens = 5, Usd = 0.5m });
            ledger.FinishRun("run-1", RunStatus.Pass, DateTimeOffset.UtcNow);
        }

        var (listExit, list) = await Run("report", "--config", ConfigDir);
        var (detailExit, detail) = await Run("report", "run-1", "--config", ConfigDir);

        Assert.Equal(0, listExit);
        Assert.Contains("demo-task", list, StringComparison.Ordinal);
        Assert.Contains("$0.5000", list, StringComparison.Ordinal);
        Assert.Equal(0, detailExit);
        Assert.Contains("worker", detail, StringComparison.Ordinal);
        Assert.Contains("PASS", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_without_config_is_a_config_error()
    {
        var (exit, output) = await Run("report", "--config", Path.Combine(_repo, "missing"));

        Assert.Equal(3, exit);
        Assert.Contains("taskrail init", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_unknown_run_fails()
    {
        await Run("init", "--path", _repo);

        var (exit, _) = await Run("report", "nope", "--config", ConfigDir);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Run_without_a_plan_is_a_usage_error()
    {
        await Run("init", "--path", _repo);

        var (exit, output) = await Run("run", "--config", ConfigDir);

        Assert.Equal(2, exit);
        Assert.Contains("taskrail plan", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_requires_models()
    {
        await Run("init", "--path", _repo);

        var (exit, output) = await Run("plan", "do something", "--config", ConfigDir);

        Assert.Equal(3, exit);
        Assert.Contains("models.architect", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bench_with_missing_taskset_is_a_usage_error()
    {
        var (exit, _) = await Run("bench", Path.Combine(_repo, "nope.yaml"));

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Unknown_command_is_usage_error()
    {
        var (exit, _) = await Run("frobnicate");

        Assert.NotEqual(0, exit);
    }
}
