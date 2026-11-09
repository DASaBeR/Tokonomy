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

    public static string NewRunId(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" +
        Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));

    public void SavePlan(PlanFile plan) => WriteText(PlanPath, JsonSerializer.Serialize(plan, JsonOptions));

    public static PlanFile LoadPlan(string path) =>
        JsonSerializer.Deserialize<PlanFile>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException($"Empty plan: {path}");

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
