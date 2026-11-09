using Taskrail.Core.Takeover;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Taskrail.Runtime.Takeover;

/// <summary>Renders <c>handoff.yaml</c>: what the architect did, what is locked, and where the worker resumes.</summary>
public static class HandoffWriter
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static string ToYaml(Handoff handoff, string? resumeFrom)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        var document = new HandoffDocument
        {
            Unit = handoff.UnitId,
            DoneByArchitect = new DoneByArchitect { Files = [.. handoff.Files], Summary = handoff.Summary, Why = handoff.Why },
            Locked = handoff.Locked.Select(l => new LockedEntry { Path = l.Path, Lines = l.Lines, Sha256 = l.Sha256 }).ToList(),
            WorkerNext = new WorkerNext { ResumeFrom = resumeFrom, MustFollow = [.. handoff.MustFollow] },
        };
        return Serializer.Serialize(document);
    }

    private sealed class HandoffDocument
    {
        public string Unit { get; set; } = "";
        public DoneByArchitect DoneByArchitect { get; set; } = new();
        public List<LockedEntry> Locked { get; set; } = [];
        public WorkerNext WorkerNext { get; set; } = new();
    }

    private sealed class DoneByArchitect
    {
        public List<string> Files { get; set; } = [];
        public string Summary { get; set; } = "";
        public string Why { get; set; } = "";
    }

    private sealed class LockedEntry
    {
        public string Path { get; set; } = "";
        public int Lines { get; set; }
        public string Sha256 { get; set; } = "";
    }

    private sealed class WorkerNext
    {
        public string? ResumeFrom { get; set; }
        public List<string> MustFollow { get; set; } = [];
    }
}
