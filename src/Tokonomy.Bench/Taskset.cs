using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Tokonomy.Bench;

/// <summary>
/// A <c>tasks.yaml</c> file. Paths are relative to the file. Acceptance files are copied in only after a run, so
/// workers never see the checks they are judged by.
/// </summary>
public sealed class Taskset
{
    /// <summary>The <c>.tokonomy</c> directory whose policy, executors, prices and ledger the benchmark uses.</summary>
    public string Config { get; set; } = ".tokonomy";

    public List<BenchTask> Tasks { get; set; } = [];

    /// <summary>Directory containing the taskset file; set on load.</summary>
    [YamlIgnore]
    public string BaseDirectory { get; private set; } = "";

    public string ConfigDirectory => Resolve(Config);

    public string Resolve(string path) => Path.GetFullPath(path, BaseDirectory);

    public static Taskset Load(string path)
    {
        var full = Path.GetFullPath(path);
        Taskset set;
        try
        {
            set = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
                .Deserialize<Taskset>(File.ReadAllText(full)) ?? new Taskset();
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(full)}:{ex.Start.Line}: {ex.InnerException?.Message ?? ex.Message}", ex);
        }

        set.BaseDirectory = Path.GetDirectoryName(full)!;
        var errors = set.Validate();
        return errors.Count == 0 ? set : throw new InvalidDataException(string.Join("\n", errors));
    }

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Tasks.Count == 0)
        {
            errors.Add("taskset has no tasks");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || !ids.Add(t.Id))
            {
                errors.Add($"task id '{t.Id}' is empty or duplicated");
            }

            if (string.IsNullOrWhiteSpace(t.Repo))
            {
                errors.Add($"task '{t.Id}': repo is required");
            }

            if (string.IsNullOrWhiteSpace(t.Prompt))
            {
                errors.Add($"task '{t.Id}': prompt is required");
            }

            if (t.Acceptance.Cmd.Count == 0)
            {
                errors.Add($"task '{t.Id}': acceptance.cmd is required (success is judged only by hidden acceptance checks)");
            }
        }

        return errors;
    }
}

public sealed class BenchTask
{
    public string Id { get; set; } = "";

    /// <summary>Local path (relative to the taskset) or git URL.</summary>
    public string Repo { get; set; } = "";

    public string Base { get; set; } = "HEAD";
    public string Prompt { get; set; } = "";
    public Acceptance Acceptance { get; set; } = new();

    public bool IsRemote => Repo.Contains("://", StringComparison.Ordinal) || Repo.StartsWith("git@", StringComparison.Ordinal);
}

public sealed class Acceptance
{
    /// <summary>Directory whose contents are copied over the result tree before the check (hidden tests).</summary>
    public string? Files { get; set; }

    public List<string> Cmd { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 900;
}
