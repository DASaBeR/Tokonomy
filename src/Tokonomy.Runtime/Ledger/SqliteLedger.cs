using System.Globalization;
using Microsoft.Data.Sqlite;
using Tokonomy.Core.Model;

namespace Tokonomy.Runtime.Ledger;

public interface ILedger
{
    void StartRun(RunRecord run);
    void SetRunStatus(string runId, RunStatus status);
    void FinishRun(string runId, RunStatus status, DateTimeOffset ended);
    void MarkEstimated(string runId);
    void RecordLlmCall(LlmCallRecord call);
    void UpsertUnit(string runId, string unitId, string name, string status, int attempts, int takeovers);
    long RecordAttempt(AttemptRecord attempt);
    void RecordGateResult(long attemptId, GateResult result, string? outputPath);
    void RecordBenchResult(BenchResultRecord result);
    IReadOnlyList<BenchResultRecord> ListBenchResults(string benchId);
    void RecordEvent(string runId, DateTimeOffset ts, string kind, string payloadJson);
    RunRecord? GetRun(string runId);
    IReadOnlyList<RunRecord> ListRuns(int limit);
    RunSummary? GetSummary(string runId);
}

/// <summary>
/// SQLite-backed ledger. Money is stored as integer micro-dollars to avoid floating-point drift.
/// Schema changes are forward-only migrations keyed on <c>PRAGMA user_version</c>.
/// </summary>
public sealed class SqliteLedger : ILedger, IDisposable
{
    private const long MicrosPerUsd = 1_000_000;

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE runs(
            id TEXT PRIMARY KEY,
            strategy TEXT NOT NULL,
            task_id TEXT NOT NULL,
            repo TEXT NOT NULL,
            base_sha TEXT NOT NULL DEFAULT '',
            status TEXT NOT NULL,
            started TEXT NOT NULL,
            ended TEXT,
            cost_source TEXT NOT NULL DEFAULT 'Metered'
        );
        CREATE TABLE units(
            id TEXT NOT NULL,
            run_id TEXT NOT NULL REFERENCES runs(id),
            name TEXT NOT NULL,
            status TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            takeovers INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(run_id, id)
        );
        CREATE TABLE attempts(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL REFERENCES runs(id),
            unit_id TEXT NOT NULL,
            n INTEGER NOT NULL,
            executor TEXT NOT NULL,
            model TEXT,
            exit_code INTEGER,
            duration_ms INTEGER,
            diff_lines INTEGER,
            decision TEXT,
            reason TEXT
        );
        CREATE TABLE gate_results(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            attempt_id INTEGER NOT NULL REFERENCES attempts(id),
            gate TEXT NOT NULL,
            status TEXT NOT NULL,
            baseline_passed INTEGER,
            new_failures_json TEXT NOT NULL DEFAULT '[]',
            output_path TEXT
        );
        CREATE TABLE llm_calls(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL REFERENCES runs(id),
            unit_id TEXT,
            role TEXT NOT NULL,
            model TEXT NOT NULL,
            input_tokens INTEGER NOT NULL,
            output_tokens INTEGER NOT NULL,
            cached_tokens INTEGER NOT NULL,
            usd_micros INTEGER NOT NULL,
            source TEXT NOT NULL
        );
        CREATE INDEX ix_llm_calls_run ON llm_calls(run_id);
        CREATE TABLE events(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL REFERENCES runs(id),
            ts TEXT NOT NULL,
            kind TEXT NOT NULL,
            payload_json TEXT NOT NULL
        );
        CREATE INDEX ix_events_run ON events(run_id);
        """,
        """
        CREATE TABLE bench_results(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            bench_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            strategy TEXT NOT NULL,
            repeat INTEGER NOT NULL,
            run_id TEXT NOT NULL,
            run_status TEXT NOT NULL,
            accepted INTEGER NOT NULL,
            usd_micros INTEGER NOT NULL,
            cost_source TEXT NOT NULL,
            duration_ms INTEGER NOT NULL,
            note TEXT
        );
        CREATE INDEX ix_bench_results_bench ON bench_results(bench_id);
        """,
    ];

    private readonly SqliteConnection _connection;

    public SqliteLedger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Migrate();
    }

    public int SchemaVersion => Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);

    public void StartRun(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Execute(
            "INSERT INTO runs(id, strategy, task_id, repo, base_sha, status, started, cost_source) VALUES($id, $s, $t, $r, $b, $st, $start, $cs)",
            ("$id", run.Id), ("$s", run.Strategy), ("$t", run.TaskId), ("$r", run.Repo), ("$b", run.BaseSha),
            ("$st", run.Status.ToString()), ("$start", Iso(run.Started)), ("$cs", run.CostSource.ToString()));
    }

    public void SetRunStatus(string runId, RunStatus status) =>
        Execute("UPDATE runs SET status = $st WHERE id = $id", ("$st", status.ToString()), ("$id", runId));

    public void FinishRun(string runId, RunStatus status, DateTimeOffset ended) =>
        Execute("UPDATE runs SET status = $st, ended = $e WHERE id = $id", ("$st", status.ToString()), ("$e", Iso(ended)), ("$id", runId));

    public void MarkEstimated(string runId) =>
        Execute("UPDATE runs SET cost_source = 'Estimated' WHERE id = $id", ("$id", runId));

    public void UpsertUnit(string runId, string unitId, string name, string status, int attempts, int takeovers) =>
        Execute(
            """
            INSERT INTO units(id, run_id, name, status, attempts, takeovers) VALUES($u, $r, $n, $s, $a, $t)
            ON CONFLICT(run_id, id) DO UPDATE SET status = $s, attempts = $a, takeovers = $t
            """,
            ("$u", unitId), ("$r", runId), ("$n", name), ("$s", status), ("$a", attempts), ("$t", takeovers));

    public long RecordAttempt(AttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        using var cmd = Command(
            """
            INSERT INTO attempts(run_id, unit_id, n, executor, model, exit_code, duration_ms, diff_lines, decision, reason)
            VALUES($r, $u, $n, $e, $m, $x, $d, $l, $dec, $why);
            SELECT last_insert_rowid();
            """,
            ("$r", attempt.RunId), ("$u", attempt.UnitId), ("$n", attempt.Number), ("$e", attempt.Executor), ("$m", attempt.Model),
            ("$x", attempt.ExitCode), ("$d", (long)attempt.Duration.TotalMilliseconds), ("$l", attempt.DiffLines),
            ("$dec", attempt.Decision.ToString()), ("$why", attempt.Reason));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void RecordGateResult(long attemptId, GateResult result, string? outputPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        Execute(
            "INSERT INTO gate_results(attempt_id, gate, status, baseline_passed, new_failures_json, output_path) VALUES($a, $g, $s, $b, $f, $o)",
            ("$a", attemptId), ("$g", result.Gate), ("$s", result.Status.ToString()),
            ("$b", result.BaselinePassed is { } bp ? (bp ? 1 : 0) : null),
            ("$f", System.Text.Json.JsonSerializer.Serialize(result.NewFailures)), ("$o", outputPath));
    }

    public void RecordBenchResult(BenchResultRecord result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Execute(
            """
            INSERT INTO bench_results(bench_id, task_id, strategy, repeat, run_id, run_status, accepted, usd_micros, cost_source, duration_ms, note)
            VALUES($b, $t, $s, $r, $run, $st, $acc, $usd, $cs, $d, $note)
            """,
            ("$b", result.BenchId), ("$t", result.TaskId), ("$s", result.Strategy), ("$r", result.Repeat), ("$run", result.RunId),
            ("$st", result.RunStatus.ToString()), ("$acc", result.Accepted ? 1 : 0), ("$usd", ToMicros(result.Usd)),
            ("$cs", result.CostSource.ToString()), ("$d", (long)result.Duration.TotalMilliseconds), ("$note", result.Note));
    }

    public IReadOnlyList<BenchResultRecord> ListBenchResults(string benchId)
    {
        using var cmd = Command(
            """
            SELECT bench_id, task_id, strategy, repeat, run_id, run_status, accepted, usd_micros, cost_source, duration_ms, note
            FROM bench_results WHERE bench_id = $b ORDER BY id
            """,
            ("$b", benchId));
        using var reader = cmd.ExecuteReader();
        var list = new List<BenchResultRecord>();
        while (reader.Read())
        {
            list.Add(new BenchResultRecord
            {
                BenchId = reader.GetString(0),
                TaskId = reader.GetString(1),
                Strategy = reader.GetString(2),
                Repeat = reader.GetInt32(3),
                RunId = reader.GetString(4),
                RunStatus = Enum.Parse<RunStatus>(reader.GetString(5)),
                Accepted = reader.GetInt64(6) == 1,
                Usd = FromMicros(reader.GetInt64(7)),
                CostSource = Enum.Parse<CostSource>(reader.GetString(8)),
                Duration = TimeSpan.FromMilliseconds(reader.GetInt64(9)),
                Note = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }

        return list;
    }

    public void RecordLlmCall(LlmCallRecord call)
    {
        ArgumentNullException.ThrowIfNull(call);
        Execute(
            """
            INSERT INTO llm_calls(run_id, unit_id, role, model, input_tokens, output_tokens, cached_tokens, usd_micros, source)
            VALUES($run, $unit, $role, $model, $in, $out, $cached, $usd, $src)
            """,
            ("$run", call.RunId), ("$unit", call.UnitId), ("$role", call.Role.ToString()), ("$model", call.Model),
            ("$in", call.InputTokens), ("$out", call.OutputTokens), ("$cached", call.CachedTokens),
            ("$usd", ToMicros(call.Usd)), ("$src", call.Source.ToString()));

        if (call.Source == CostSource.Estimated)
        {
            Execute("UPDATE runs SET cost_source = 'Estimated' WHERE id = $id", ("$id", call.RunId));
        }
    }

    public void RecordEvent(string runId, DateTimeOffset ts, string kind, string payloadJson) =>
        Execute("INSERT INTO events(run_id, ts, kind, payload_json) VALUES($r, $ts, $k, $p)",
            ("$r", runId), ("$ts", Iso(ts)), ("$k", kind), ("$p", payloadJson));

    public RunRecord? GetRun(string runId) => QueryRuns("WHERE r.id = $id", 1, ("$id", runId)) is [var run, ..] ? run : null;

    public IReadOnlyList<RunRecord> ListRuns(int limit) => QueryRuns("", limit);

    public RunSummary? GetSummary(string runId)
    {
        var run = GetRun(runId);
        if (run is null)
        {
            return null;
        }

        using var cmd = Command(
            """
            SELECT role, COUNT(*), SUM(input_tokens), SUM(output_tokens), SUM(usd_micros), MAX(source = 'Estimated')
            FROM llm_calls WHERE run_id = $id GROUP BY role ORDER BY role
            """,
            ("$id", runId));
        using var reader = cmd.ExecuteReader();
        var tiers = new List<TierTotals>();
        while (reader.Read())
        {
            tiers.Add(new TierTotals(
                Enum.Parse<LlmRole>(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                FromMicros(reader.GetInt64(4)),
                reader.GetInt64(5) == 1));
        }

        return new RunSummary { Run = run, Tiers = tiers };
    }

    public void Dispose() => _connection.Dispose();

    private List<RunRecord> QueryRuns(string where, int limit, params (string, object?)[] args)
    {
        using var cmd = Command(
            $"""
            SELECT r.id, r.strategy, r.task_id, r.repo, r.base_sha, r.status, r.started, r.ended, r.cost_source,
                   COALESCE((SELECT SUM(usd_micros) FROM llm_calls c WHERE c.run_id = r.id), 0)
            FROM runs r {where} ORDER BY r.started DESC LIMIT $limit
            """,
            [.. args, ("$limit", limit)]);
        using var reader = cmd.ExecuteReader();
        var runs = new List<RunRecord>();
        while (reader.Read())
        {
            runs.Add(new RunRecord
            {
                Id = reader.GetString(0),
                Strategy = reader.GetString(1),
                TaskId = reader.GetString(2),
                Repo = reader.GetString(3),
                BaseSha = reader.GetString(4),
                Status = Enum.Parse<RunStatus>(reader.GetString(5)),
                Started = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                Ended = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                CostSource = Enum.Parse<CostSource>(reader.GetString(8)),
                TotalUsd = FromMicros(reader.GetInt64(9)),
            });
        }

        return runs;
    }

    private void Migrate()
    {
        var version = SchemaVersion;
        if (version > Migrations.Length)
        {
            throw new InvalidOperationException(
                $"Ledger schema version {version} is newer than this Tokonomy build supports ({Migrations.Length}). Upgrade Tokonomy.");
        }

        for (var i = version; i < Migrations.Length; i++)
        {
            using var tx = _connection.BeginTransaction();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Migrations[i] + $"\nPRAGMA user_version = {i + 1};";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    private void Execute(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = Command(sql);
        return cmd.ExecuteScalar();
    }

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static long ToMicros(decimal usd) => (long)Math.Round(usd * MicrosPerUsd, MidpointRounding.AwayFromZero);

    internal static decimal FromMicros(long micros) => micros / (decimal)MicrosPerUsd;
}
