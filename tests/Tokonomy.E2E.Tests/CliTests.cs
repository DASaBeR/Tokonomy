using System.Text.Json;
using Spectre.Console;
using Tokonomy.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Ledger;

namespace Tokonomy.E2E.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "tokonomy-e2e", Guid.NewGuid().ToString("N"));

    public CliTests()
    {
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_repo, "pyproject.toml"), "");
    }

    private string ConfigDir => Path.Combine(_repo, ".tokonomy");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            // Init now creates a real git repository; git writes its objects read-only.
            foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repo, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        var exit = await TokonomyApp.RunAsync(args, console);
        return (exit, writer.ToString());
    }

    [Fact]
    public async Task Init_scaffolds_config_for_detected_stack()
    {
        var (exit, output) = await Run("init", "--strong", "claude", "--cheap", "antigravity", "--path", _repo);

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
        var (exit, output) = await Run("init", "--strong", "claude", "--cheap", "antigravity", "--path", _repo, "--json");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("python", doc.RootElement.GetProperty("stacks")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(RunStatus.Pass, "already passed")]
    [InlineData(RunStatus.Planned, "never started")]
    [InlineData(RunStatus.Fail, "failed on a verdict")]
    [InlineData(RunStatus.Running, "no saved plan")]
    public async Task Resume_refuses_runs_that_are_not_resumable(RunStatus status, string message)
    {
        await Run("init", "--strong", "claude", "--cheap", "antigravity", "--path", _repo);
        using (var ledger = new SqliteLedger(Path.Combine(ConfigDir, "ledger.db")))
        {
            ledger.StartRun(new RunRecord { Id = "run-r", Strategy = "4", TaskId = "t", Repo = _repo, Status = status, Started = DateTimeOffset.UtcNow });
        }

        var (exit, output) = await Run("run", "--resume", "run-r", "--config", ConfigDir);

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains(message, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_cannot_be_combined_with_plan_or_again()
    {
        await Run("init", "--strong", "claude", "--cheap", "antigravity", "--path", _repo);

        var (exit, output) = await Run("run", "--resume", "a", "--plan", "b", "--config", ConfigDir);

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("cannot be combined", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_shows_architect_spend_by_purpose_interventions_revisions_and_reviews()
    {
        await Run("init", "--strong", "claude", "--cheap", "antigravity", "--path", _repo);
        using (var ledger = new SqliteLedger(Path.Combine(ConfigDir, "ledger.db")))
        {
            ledger.StartRun(new RunRecord { Id = "run-5", Strategy = "5", TaskId = "adaptive", Repo = _repo, Started = DateTimeOffset.UtcNow });
            ledger.RecordLlmCall(new LlmCallRecord { RunId = "run-5", Role = LlmRole.Architect, Model = "opus", Usd = 0.2m, Purpose = "planning" });
            ledger.RecordLlmCall(new LlmCallRecord { RunId = "run-5", Role = LlmRole.Architect, Model = "opus", Usd = 0.05m, Purpose = "supervision" });
            ledger.UpsertUnit("run-5", "U1", "Search", "accepted", 2, 0);
            ledger.RecordIntervention(new InterventionRecord
            {
                RunId = "run-5", UnitId = "U1", At = DateTimeOffset.UtcNow, Trigger = "worker_blocked", Level = 1, Outcome = "resumed",
                Diagnosis = "plan named [IProductService] which does not exist", Action = "corrected instructions given", PlanRevision = 2,
            });
            ledger.RecordPlanRevision("run-5", new Tokonomy.Core.Planning.PlanRevision
            {
                Number = 2, At = DateTimeOffset.UtcNow, Source = "supervision", Reason = "use [ProductQueries]", Changes = [],
            });
            ledger.RecordUnitReview(new UnitReviewRecord("run-5", "U1", DateTimeOffset.UtcNow, UnitReviewVerdict.ApproveWithWarnings, "ok [minor]", "high-risk unit",
                [new ReviewFinding("low", "other", "a.py", "naming [nit]")]));
            ledger.FinishRun("run-5", RunStatus.Pass, DateTimeOffset.UtcNow);
        }

        var (exit, detail) = await Run("report", "run-5", "--config", ConfigDir);

        Assert.Equal(0, exit);
        Assert.Contains("supervision 1×", detail, StringComparison.Ordinal);
        Assert.Contains("[IProductService]", detail, StringComparison.Ordinal);
        Assert.Contains("rev 2", detail, StringComparison.Ordinal);
        Assert.Contains("ApproveWithWarnings", detail, StringComparison.Ordinal);
        Assert.Contains("naming [nit]", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_lists_runs_from_ledger()
    {
        await Run("init", "--strong", "claude", "--cheap", "antigravity", "--path", _repo);
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
        Assert.Contains("tokonomy init", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_unknown_run_fails()
    {
        await Run("init", "--strong", "claude", "--cheap", "antigravity", "--path", _repo);

        var (exit, _) = await Run("report", "nope", "--config", ConfigDir);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Run_without_a_plan_is_a_usage_error()
    {
        await Run("init", "--strong", "claude", "--cheap", "antigravity", "--path", _repo);

        var (exit, output) = await Run("run", "--config", ConfigDir);

        Assert.Equal(2, exit);
        Assert.Contains("tokonomy plan", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_requires_models()
    {
        await Run("init", "--path", _repo, "--preset", "api", "--architect", "", "--worker", "");

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
