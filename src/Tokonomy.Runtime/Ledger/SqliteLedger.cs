using System.Globalization;
using System.Text.Json;
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
    void RecordIntervention(InterventionRecord intervention);
    void RecordPlanRevision(string runId, Tokonomy.Core.Planning.PlanRevision revision);
    void RecordUnitReview(UnitReviewRecord review);
    RunStats GetStats(string runId);
    IReadOnlyList<AttemptRecord> ListAttempts(string runId);
    IReadOnlyList<InterventionRecord> ListInterventions(string runId);
    IReadOnlyList<PlanRevisionRecord> ListPlanRevisions(string runId);
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
        """
        ALTER TABLE llm_calls ADD COLUMN purpose TEXT;
        CREATE TABLE plan_revisions(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL REFERENCES runs(id),
            revision INTEGER NOT NULL,
            ts TEXT NOT NULL,
            source TEXT NOT NULL,
            reason TEXT NOT NULL,
            trigger TEXT,
            changes_json TEXT NOT NULL,
            requires_approval INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_plan_revisions_run ON plan_revisions(run_id);
        CREATE TABLE interventions(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL REFERENCES runs(id),
            unit_id TEXT NOT NULL,
            ts TEXT NOT NULL,
            trigger TEXT NOT NULL,
            level INTEGER NOT NULL,
            root_cause TEXT,
            diagnosis TEXT,
            action TEXT,
            outcome TEXT NOT NULL,
            plan_revision INTEGER,
            evidence_json TEXT NOT NULL DEFAULT '[]',
            usd_micros INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_interventions_run ON interventions(run_id);
        CREATE TABLE unit_reviews(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL REFERENCES runs(id),
            unit_id TEXT NOT NULL,
            ts TEXT NOT NULL,
            verdict TEXT NOT NULL,
            summary TEXT,
            why TEXT,
            findings_json TEXT NOT NULL DEFAULT '[]'
        );
        CREATE INDEX ix_unit_reviews_run ON unit_reviews(run_id);
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

        // Token use per model comes from the llm_calls of each trial's run.
        reader.Close();
        using var usage = Command(
            """
            SELECT c.run_id, c.model, COUNT(*), SUM(c.input_tokens), SUM(c.output_tokens), SUM(c.cached_tokens), SUM(c.usd_micros)
            FROM llm_calls c JOIN bench_results b ON b.run_id = c.run_id
            WHERE b.bench_id = $b GROUP BY c.run_id, c.model ORDER BY c.run_id, c.model
            """,
            ("$b", benchId));
        using var rows = usage.ExecuteReader();
        var byRun = new Dictionary<string, List<ModelUsage>>(StringComparer.Ordinal);
        while (rows.Read())
        {
            var runId = rows.GetString(0);
            if (!byRun.TryGetValue(runId, out var models))
            {
                byRun[runId] = models = [];
            }

            models.Add(new ModelUsage(rows.GetString(1), rows.GetInt32(2), rows.GetInt64(3), rows.GetInt64(4), rows.GetInt64(5), FromMicros(rows.GetInt64(6))));
        }

        rows.Close();
        return list.Select(r => (byRun.TryGetValue(r.RunId, out var m) ? r with { Models = m } : r) with { Stats = GetStats(r.RunId) }).ToList();
    }

    public void RecordLlmCall(LlmCallRecord call)
    {
        ArgumentNullException.ThrowIfNull(call);
        Execute(
            """
            INSERT INTO llm_calls(run_id, unit_id, role, model, input_tokens, output_tokens, cached_tokens, usd_micros, source, purpose)
            VALUES($run, $unit, $role, $model, $in, $out, $cached, $usd, $src, $purpose)
            """,
            ("$run", call.RunId), ("$unit", call.UnitId), ("$role", call.Role.ToString()), ("$model", call.Model),
            ("$in", call.InputTokens), ("$out", call.OutputTokens), ("$cached", call.CachedTokens),
            ("$usd", ToMicros(call.Usd)), ("$src", call.Source.ToString()), ("$purpose", call.Purpose));

        if (call.Source == CostSource.Estimated)
        {
            Execute("UPDATE runs SET cost_source = 'Estimated' WHERE id = $id", ("$id", call.RunId));
        }
    }

    public void RecordEvent(string runId, DateTimeOffset ts, string kind, string payloadJson) =>
        Execute("INSERT INTO events(run_id, ts, kind, payload_json) VALUES($r, $ts, $k, $p)",
            ("$r", runId), ("$ts", Iso(ts)), ("$k", kind), ("$p", payloadJson));

    public void RecordIntervention(InterventionRecord intervention)
    {
        ArgumentNullException.ThrowIfNull(intervention);
        Execute(
            """
            INSERT INTO interventions(run_id, unit_id, ts, trigger, level, root_cause, diagnosis, action, outcome, plan_revision, evidence_json, usd_micros)
            VALUES($r, $u, $ts, $t, $l, $rc, $d, $a, $o, $pr, $e, $usd)
            """,
            ("$r", intervention.RunId), ("$u", intervention.UnitId), ("$ts", Iso(intervention.At)), ("$t", intervention.Trigger),
            ("$l", intervention.Level), ("$rc", intervention.RootCause), ("$d", intervention.Diagnosis), ("$a", intervention.Action),
            ("$o", intervention.Outcome), ("$pr", intervention.PlanRevision), ("$e", JsonSerializer.Serialize(intervention.Evidence)),
            ("$usd", ToMicros(intervention.Usd)));
    }

    public void RecordPlanRevision(string runId, Tokonomy.Core.Planning.PlanRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        Execute(
            """
            INSERT INTO plan_revisions(run_id, revision, ts, source, reason, trigger, changes_json, requires_approval)
            VALUES($r, $n, $ts, $s, $why, $t, $c, $h)
            """,
            ("$r", runId), ("$n", revision.Number), ("$ts", Iso(revision.At)), ("$s", revision.Source), ("$why", revision.Reason),
            ("$t", revision.Trigger), ("$c", JsonSerializer.Serialize(revision.Changes)), ("$h", revision.RequiresHumanApproval ? 1 : 0));
    }

    public void RecordUnitReview(UnitReviewRecord review)
    {
        ArgumentNullException.ThrowIfNull(review);
        Execute(
            "INSERT INTO unit_reviews(run_id, unit_id, ts, verdict, summary, why, findings_json) VALUES($r, $u, $ts, $v, $s, $w, $f)",
            ("$r", review.RunId), ("$u", review.UnitId), ("$ts", Iso(review.At)), ("$v", review.Verdict.ToString()), ("$s", review.Summary),
            ("$w", review.Why), ("$f", JsonSerializer.Serialize(review.Findings)));
    }

    public RunStats GetStats(string runId)
    {
        long Count(string sql) => Convert.ToInt64(ScalarWith(sql, ("$id", runId)) ?? 0L, CultureInfo.InvariantCulture);
        var accepted = (int)Count("SELECT COUNT(*) FROM units WHERE run_id = $id AND status = 'accepted'");
        var workerAttempts = (int)Count("SELECT COUNT(*) FROM attempts WHERE run_id = $id AND executor <> 'architect'");
        var unitsTried = (int)Count("SELECT COUNT(DISTINCT unit_id) FROM attempts WHERE run_id = $id AND executor <> 'architect'");
        var takenOverUnits = (int)Count("SELECT COUNT(DISTINCT unit_id) FROM attempts WHERE run_id = $id AND executor = 'architect' AND reason LIKE 'takeover%' AND decision = 'Accept'");
        var findings = 0;
        var changeRequests = 0;
        using (var cmd = Command("SELECT verdict, findings_json FROM unit_reviews WHERE run_id = $id", ("$id", runId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                changeRequests += reader.GetString(0) == nameof(UnitReviewVerdict.RequestChanges) ? 1 : 0;
                var list = JsonSerializer.Deserialize<List<ReviewFinding>>(reader.GetString(1)) ?? [];
                findings += list.Count(f => f.High);
            }
        }

        var lessons = 0;
        using (var cmd = Command("SELECT changes_json FROM plan_revisions WHERE run_id = $id", ("$id", runId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var changes = JsonSerializer.Deserialize<List<Tokonomy.Core.Planning.PlanChange>>(reader.GetString(0)) ?? [];
                lessons += changes.Count(c => c.Op == Tokonomy.Core.Planning.PlanOps.AddLesson);
            }
        }

        var byPurpose = new Dictionary<string, decimal>(StringComparer.Ordinal);
        using (var cmd = Command(
                   "SELECT role, COALESCE(purpose, CASE role WHEN 'Worker' THEN 'work' ELSE 'unspecified' END), SUM(usd_micros) FROM llm_calls WHERE run_id = $id GROUP BY 1, 2",
                   ("$id", runId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                byPurpose[$"{reader.GetString(0).ToLowerInvariant()}:{reader.GetString(1)}"] = FromMicros(reader.GetInt64(2));
            }
        }

        int? unplanned = null;
        string? judge = null;
        List<string> concerns = [];
        if (ScalarWith("SELECT payload_json FROM events WHERE run_id = $id AND kind = 'bench_quality' ORDER BY id DESC LIMIT 1", ("$id", runId)) is string quality)
        {
            using var doc = JsonDocument.Parse(quality);
            var root = doc.RootElement;
            unplanned = root.TryGetProperty("unplanned", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : null;
            judge = root.TryGetProperty("judge", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;
            if (root.TryGetProperty("concerns", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                concerns = c.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            }
        }

        return new RunStats
        {
            BlockerReports = (int)Count("SELECT COUNT(*) FROM events WHERE run_id = $id AND kind = 'worker_report' AND payload_json LIKE '%\"needs_architect\":true%'"),
            FailedTakeovers = (int)Count("SELECT COUNT(*) FROM attempts WHERE run_id = $id AND executor = 'architect' AND reason LIKE 'takeover%' AND decision <> 'Accept'"),
            Splits = (int)Count("SELECT COUNT(*) FROM interventions WHERE run_id = $id AND outcome = 'split'"),
            SupervisedTakeovers = (int)Count("SELECT COUNT(*) FROM interventions WHERE run_id = $id AND level = 3"),
            Lessons = lessons,
            UsdByPurpose = byPurpose,
            UnplannedChanges = unplanned,
            JudgeVerdict = judge,
            JudgeConcerns = concerns,
            Units = (int)Count("SELECT COUNT(*) FROM units WHERE run_id = $id"),
            UnitsAccepted = accepted,
            UnitsAcceptedWithoutTakeover = Math.Max(0, accepted - takenOverUnits),
            WorkerAttempts = workerAttempts,
            WorkerRetries = Math.Max(0, workerAttempts - unitsTried),
            Takeovers = (int)Count("SELECT COUNT(*) FROM attempts WHERE run_id = $id AND executor = 'architect' AND reason LIKE 'takeover%'"),
            Interventions = (int)Count("SELECT COUNT(*) FROM interventions WHERE run_id = $id"),
            InstructionCorrections = (int)Count("SELECT COUNT(*) FROM interventions WHERE run_id = $id AND level = 1"),
            GuidedCorrections = (int)Count("SELECT COUNT(*) FROM interventions WHERE run_id = $id AND level = 2"),
            PlanRevisions = (int)Count("SELECT COUNT(*) FROM plan_revisions WHERE run_id = $id"),
            ReviewChangeRequests = changeRequests,
            ArchitecturalViolations = findings,
            RepeatedMistakes = (int)Count("SELECT COUNT(*) FROM gate_results g JOIN attempts a ON a.id = g.attempt_id WHERE a.run_id = $id AND g.gate = 'lessons' AND g.status = 'Failed'"),
            HumanDecisions = (int)Count("SELECT COUNT(*) FROM interventions WHERE run_id = $id AND outcome = 'human'"),
        };
    }

    public IReadOnlyList<AttemptRecord> ListAttempts(string runId)
    {
        using var cmd = Command(
            "SELECT unit_id, n, executor, model, exit_code, duration_ms, diff_lines, decision, reason FROM attempts WHERE run_id = $id ORDER BY id",
            ("$id", runId));
        using var reader = cmd.ExecuteReader();
        var list = new List<AttemptRecord>();
        while (reader.Read())
        {
            list.Add(new AttemptRecord
            {
                RunId = runId,
                UnitId = reader.GetString(0),
                Number = reader.GetInt32(1),
                Executor = reader.GetString(2),
                Model = reader.IsDBNull(3) ? null : reader.GetString(3),
                ExitCode = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Duration = TimeSpan.FromMilliseconds(reader.IsDBNull(5) ? 0 : reader.GetInt64(5)),
                DiffLines = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Decision = Enum.TryParse<DecisionKind>(reader.IsDBNull(7) ? "" : reader.GetString(7), out var d) ? d : DecisionKind.Fail,
                Reason = reader.IsDBNull(8) ? "" : reader.GetString(8),
            });
        }

        return list;
    }

    public IReadOnlyList<InterventionRecord> ListInterventions(string runId)
    {
        using var cmd = Command(
            "SELECT unit_id, ts, trigger, level, root_cause, diagnosis, action, outcome, plan_revision, evidence_json, usd_micros FROM interventions WHERE run_id = $id ORDER BY id",
            ("$id", runId));
        using var reader = cmd.ExecuteReader();
        var list = new List<InterventionRecord>();
        while (reader.Read())
        {
            list.Add(new InterventionRecord
            {
                RunId = runId,
                UnitId = reader.GetString(0),
                At = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Trigger = reader.GetString(2),
                Level = reader.GetInt32(3),
                RootCause = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Diagnosis = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Action = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Outcome = reader.GetString(7),
                PlanRevision = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Evidence = JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? [],
                Usd = FromMicros(reader.GetInt64(10)),
            });
        }

        return list;
    }

    public IReadOnlyList<PlanRevisionRecord> ListPlanRevisions(string runId)
    {
        using var cmd = Command("SELECT revision, ts, source, reason, trigger, changes_json, requires_approval FROM plan_revisions WHERE run_id = $id ORDER BY id", ("$id", runId));
        using var reader = cmd.ExecuteReader();
        var list = new List<PlanRevisionRecord>();
        while (reader.Read())
        {
            var changes = JsonSerializer.Deserialize<List<Tokonomy.Core.Planning.PlanChange>>(reader.GetString(5)) ?? [];
            list.Add(new PlanRevisionRecord(runId, reader.GetInt32(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), changes.Count, reader.GetInt64(6) == 1));
        }

        return list;
    }

    public IReadOnlyList<UnitReviewRecord> ListUnitReviews(string runId)
    {
        using var cmd = Command("SELECT unit_id, ts, verdict, summary, why, findings_json FROM unit_reviews WHERE run_id = $id ORDER BY id", ("$id", runId));
        using var reader = cmd.ExecuteReader();
        var list = new List<UnitReviewRecord>();
        while (reader.Read())
        {
            list.Add(new UnitReviewRecord(runId, reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Enum.Parse<UnitReviewVerdict>(reader.GetString(2)), reader.IsDBNull(3) ? "" : reader.GetString(3), reader.IsDBNull(4) ? "" : reader.GetString(4),
                JsonSerializer.Deserialize<List<ReviewFinding>>(reader.GetString(5)) ?? []));
        }

        return list;
    }

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

        reader.Close();
        using var byPurpose = Command(
            """
            SELECT role, COALESCE(purpose, CASE role WHEN 'Worker' THEN 'work' ELSE 'unspecified' END), COUNT(*), SUM(input_tokens), SUM(output_tokens),
                   SUM(usd_micros), MAX(source = 'Estimated')
            FROM llm_calls WHERE run_id = $id GROUP BY 1, 2 ORDER BY 1, 6 DESC
            """,
            ("$id", runId));
        using var rows = byPurpose.ExecuteReader();
        var purposes = new List<PurposeTotals>();
        while (rows.Read())
        {
            purposes.Add(new PurposeTotals(Enum.Parse<LlmRole>(rows.GetString(0)), rows.GetString(1), rows.GetInt32(2), rows.GetInt64(3),
                rows.GetInt64(4), FromMicros(rows.GetInt64(5)), rows.GetInt64(6) == 1));
        }

        rows.Close();
        return new RunSummary
        {
            Run = run,
            Tiers = tiers,
            Purposes = purposes,
            Stats = GetStats(runId),
            Interventions = ListInterventions(runId),
            Revisions = ListPlanRevisions(runId),
            Reviews = ListUnitReviews(runId),
        };
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

    private object? ScalarWith(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteScalar();
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
