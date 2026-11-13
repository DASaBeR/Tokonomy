using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Ledger;

namespace Tokonomy.Runtime.Orchestration;

/// <summary>Metadata saved by <c>tokonomy plan</c> and read by <c>tokonomy run</c>.</summary>
public sealed record PlanFile
{
    public required string RunId { get; init; }
    public required string Task { get; init; }
    public required string Strategy { get; init; }
    public required string BaseSha { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required Contract Contract { get; init; }

    /// <summary>The plan's engineering-quality verdict when it was made. Null for plans saved by older builds.</summary>
    public Tokonomy.Core.Planning.PlanReadiness? Readiness { get; init; }

    /// <summary>The quality issues found (deterministic checks and the critique), for <c>run</c> to show again.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
}

/// <summary>Layout of <c>.tokonomy/runs/&lt;run-id&gt;/</c>. Everything written here is redacted first.</summary>
public sealed class RunArtifacts
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly IReadOnlyList<string> _secrets;
    private readonly Lock _transcriptLock = new();

    public RunArtifacts(string configDirectory, string runId, IReadOnlyList<string>? secrets = null)
    {
        Directory = Path.Combine(configDirectory, "runs", runId);
        _secrets = secrets ?? [];
    }

    public string Directory { get; }
    public string PlanPath => Path.Combine(Directory, "plan.json");
    public string LogsDirectory => Path.Combine(Directory, "logs");
    public string TranscriptPath => Path.Combine(Directory, "transcript.jsonl");

    /// <summary>The execution plan as it evolves: original, current revision, history, lessons and unit progress.</summary>
    public string PlanStatePath => Path.Combine(Directory, "plan-state.json");

    public static string NewRunId(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" +
        Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));

    public void SavePlan(PlanFile plan) => WriteText(PlanPath, JsonSerializer.Serialize(plan, JsonOptions));

    public static PlanFile LoadPlan(string path) =>
        JsonSerializer.Deserialize<PlanFile>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException($"Empty plan: {path}");

    /// <summary>
    /// Saves the plan state atomically (write a temporary file, then replace), so an interrupted run never leaves a
    /// half-written state behind.
    /// </summary>
    public void SavePlanState(Tokonomy.Core.Planning.PlanState state)
    {
        WriteAtomic(PlanStatePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    public Tokonomy.Core.Planning.PlanState? LoadPlanState() =>
        File.Exists(PlanStatePath) ? JsonSerializer.Deserialize<Tokonomy.Core.Planning.PlanState>(File.ReadAllText(PlanStatePath), JsonOptions) : null;

    /// <summary>The orchestrator's last checkpoint (see <see cref="RunState"/>).</summary>
    public string RunStatePath => Path.Combine(Directory, "run-state.json");

    public void SaveRunState(RunState state) => WriteAtomic(RunStatePath, JsonSerializer.Serialize(state, JsonOptions));

    public RunState? LoadRunState() =>
        File.Exists(RunStatePath) ? JsonSerializer.Deserialize<RunState>(File.ReadAllText(RunStatePath), JsonOptions) : null;

    /// <summary>
    /// Takes the run's lock file for as long as the returned handle lives. The operating system releases it when the
    /// process dies, so a crashed run can be resumed while a live one cannot be entered twice.
    /// </summary>
    public IDisposable AcquireLock()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, "run.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            var pid = Encoding.UTF8.GetBytes($"{Environment.ProcessId} {DateTimeOffset.UtcNow:O}{Environment.NewLine}");
            stream.Write(pid);
            stream.Flush();
            return stream;
        }
        catch (IOException)
        {
            throw new RunInProgressException($"run {System.IO.Path.GetFileName(Directory)} is in use by another Tokonomy process; wait for it to finish (or stop it) before resuming");
        }
    }

    private void WriteAtomic(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Redactor.Redact(content, _secrets), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Writes a (redacted) file directly under the run directory.</summary>
    public string WriteFile(string name, string content)
    {
        var path = Path.Combine(Directory, name);
        WriteText(path, content);
        return path;
    }

    public string WriteLog(string name, string content)
    {
        var path = Path.Combine(LogsDirectory, name);
        WriteText(path, content);
        return path;
    }

    public void Append(string kind, object payload)
    {
        var line = JsonSerializer.Serialize(new { ts = DateTimeOffset.UtcNow, kind, payload }, JsonOptions).ReplaceLineEndings(" ");
        lock (_transcriptLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(TranscriptPath, Redactor.Redact(line, _secrets) + "\n", new UTF8Encoding(false));
        }
    }

    private void WriteText(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Redactor.Redact(content, _secrets), new UTF8Encoding(false));
    }
}
