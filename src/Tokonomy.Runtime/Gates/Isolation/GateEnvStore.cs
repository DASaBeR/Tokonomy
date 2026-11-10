using System.Text.Json;
using System.Text.Json.Serialization;
using Tokonomy.Runtime.Git;

namespace Tokonomy.Runtime.Gates.Isolation;

/// <summary>What Tokonomy records about an environment before it starts it, so a crashed run's leftovers can be found.</summary>
public sealed record GateEnvRecord
{
    [JsonPropertyName("env_id")] public required string EnvId { get; init; }
    [JsonPropertyName("run_id")] public required string RunId { get; init; }
    [JsonPropertyName("provider")] public required string Provider { get; init; }
    [JsonPropertyName("owner_pid")] public int OwnerPid { get; init; }
    [JsonPropertyName("owner_started_utc")] public DateTime OwnerStartedUtc { get; init; }
    [JsonPropertyName("created_utc")] public DateTime CreatedUtc { get; init; }
    [JsonPropertyName("sandbox_id")] public string? SandboxId { get; init; }

    [JsonIgnore] public string Directory { get; init; } = "";
}

/// <summary>
/// Host-side folders of gate environments, one per environment, under <c>%LOCALAPPDATA%\tokonomy\gate-envs</c> by default
/// (outside temp and the repository). Each holds <c>state.json</c>, written <b>before</b> anything is started.
/// Everything here is deleted with <see cref="WorktreeGuard.DeleteTree"/>, which never follows a link.
/// </summary>
public sealed class GateEnvStore(string root)
{
    public const string StateFile = "state.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>The default root, next to the worktree root.</summary>
    public static string? DefaultRoot() =>
        WorktreeRoot.Default() is { } worktrees ? Path.Combine(Path.GetDirectoryName(worktrees)!, "gate-envs") : null;

    public GateEnvRecord Create(string runId, string label, string provider)
    {
        Directory.CreateDirectory(Root);
        if (WorktreeGuard.IsLink(Root))
        {
            throw new GateEnvironmentException($"refusing to use {Root} for gate environments: it is a link");
        }

        var safeLabel = new string(label.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        var id = $"{runId}-{safeLabel}-{Guid.NewGuid().ToString("N")[..6]}";
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var record = new GateEnvRecord
        {
            EnvId = id,
            RunId = runId,
            Provider = provider,
            OwnerPid = self.Id,
            OwnerStartedUtc = self.StartTime.ToUniversalTime(),
            CreatedUtc = DateTime.UtcNow,
            Directory = dir,
        };
        Save(record);
        return record;
    }

    public void Save(GateEnvRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!WorktreeGuard.IsInside(Root, record.Directory))
        {
            throw new GateEnvironmentException($"{record.Directory} is not under {Root}");
        }

        var path = Path.Combine(record.Directory, StateFile);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Every recorded environment. Folders without a readable state file are reported with an empty run id.</summary>
    public IReadOnlyList<GateEnvRecord> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var records = new List<GateEnvRecord>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (WorktreeGuard.IsLink(dir))
            {
                continue;   // never ours; never followed
            }

            GateEnvRecord? record = null;
            try
            {
                var text = WorktreeGuard.ReadAllText(dir, StateFile, maxBytes: 64 * 1024);
                record = text is null ? null : JsonSerializer.Deserialize<GateEnvRecord>(text);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }

            records.Add((record ?? new GateEnvRecord { EnvId = Path.GetFileName(dir), RunId = "", Provider = "" }) with { Directory = dir });
        }

        return records;
    }

    /// <summary>True when the process that created the environment is gone (or the pid now belongs to another process).</summary>
    public static bool IsOrphaned(GateEnvRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.OwnerPid <= 0)
        {
            return true;
        }

        try
        {
            using var owner = System.Diagnostics.Process.GetProcessById(record.OwnerPid);
            return Math.Abs((owner.StartTime.ToUniversalTime() - record.OwnerStartedUtc).TotalSeconds) > 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>Deletes an environment folder. Only folders directly under <see cref="Root"/> are ever deleted.</summary>
    public string? Delete(GateEnvRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var dir = Path.GetFullPath(record.Directory);
        if (!string.Equals(Path.GetDirectoryName(dir), Root.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return $"{dir} is not a gate environment folder under {Root}; not deleting it";
        }

        return WorktreeGuard.DeleteTree(dir);
    }
}
