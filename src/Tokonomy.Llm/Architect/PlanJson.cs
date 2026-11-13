using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;

namespace Tokonomy.Llm.Architect;

/// <summary>
/// JSON shapes the architect writes (plans, units, plan operations) and their mapping to the model. Lenient where models
/// are sloppy (field aliases, <c>files</c> as an array or an object), strict where it matters (validation happens after).
/// </summary>
internal static partial class PlanJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>The JSON object in a reply: the first fenced <c>```json</c> block if any, else the outermost braces.</summary>
    public static string? ExtractJson(string text)
    {
        var fenced = FencedJson().Match(text);
        var candidate = fenced.Success ? fenced.Groups[1].Value : text;
        var start = candidate.IndexOf('{', StringComparison.Ordinal);
        var end = candidate.LastIndexOf('}');
        return start >= 0 && end > start ? candidate[start..(end + 1)] : null;
    }

    /// <summary>Splits a reply into its fenced JSON block and the rest (where edit blocks live).</summary>
    public static (string? Json, string Remainder) SplitFenced(string text)
    {
        var fenced = FencedJson().Match(text);
        if (fenced.Success)
        {
            return (fenced.Groups[1].Value, text.Remove(fenced.Index, fenced.Length));
        }

        // No fence: the JSON object must come first; everything after its closing brace is the rest.
        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return (null, text);
        }

        var end = MatchingBrace(text, start);
        return end < 0 ? (null, text) : (text[start..(end + 1)], text[(end + 1)..]);
    }

    public static T? Deserialize<T>(string? json)
        where T : class
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The plan as compact JSON for a prompt, with acceptance test contents replaced by their paths.</summary>
    public static string Describe(Contract contract)
    {
        var shown = contract with
        {
            Units = contract.Units.Select(u => u with { Tests = u.Tests.Select(t => new AcceptanceTestFile(t.Path, "(test file)")).ToList() }).ToList(),
        };
        return JsonSerializer.Serialize(shown, Compact);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Compact);

    public static Contract ToContract(string task, ContractDto dto)
    {
        var units = (dto.Units ?? []).Select(u => u.ToUnit()).ToList();
        var blueprint = dto.Analysis is not null || dto.Architecture is not null || dto.Decisions is { Count: > 0 } ||
                        units.Any(u => u.Steps.Count > 0 || u.Objective is not null);
        return new Contract
        {
            Task = task,
            Units = units,
            Rules = (dto.Rules ?? []).Select((r, i) => new Rule(string.IsNullOrWhiteSpace(r.Id) ? $"R{i + 1}" : r.Id.Trim(), r.Text?.Trim() ?? "")).ToList(),
            DontList = Clean(dto.Dont),
            SchemaVersion = dto.SchemaVersion is { } v and > 0 ? v : blueprint ? Contract.BlueprintSchemaVersion : 1,
            Analysis = dto.Analysis?.ToModel(),
            Architecture = dto.Architecture?.ToModel(),
            Decisions = (dto.Decisions ?? []).Where(d => !string.IsNullOrWhiteSpace(d.Id)).Select(d => d.ToModel()).ToList(),
        };
    }

    public static List<string> Clean(IEnumerable<string?>? items) =>
        (items ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct(StringComparer.Ordinal).ToList();

    public static UnitRisk ParseRisk(string? risk) => risk?.Trim().ToLowerInvariant() switch
    {
        "high" or "critical" => UnitRisk.High,
        "medium" or "moderate" => UnitRisk.Medium,
        _ => UnitRisk.Low,
    };

    private static int MatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline)]
    private static partial Regex FencedJson();

    internal sealed class ContractDto
    {
        public int? SchemaVersion { get; set; }
        public AnalysisDto? Analysis { get; set; }
        public ArchitectureDto? Architecture { get; set; }
        public List<DecisionDto>? Decisions { get; set; }
        public List<UnitDto>? Units { get; set; }
        public List<RuleDto>? Rules { get; set; }
        public List<string>? Dont { get; set; }

        /// <summary>Set when the architect asks to read files instead of answering with a plan.</summary>
        public List<string>? ReadFiles { get; set; }
    }

    internal sealed class AnalysisDto
    {
        public List<FactDto>? Facts { get; set; }
        public List<string>? Assumptions { get; set; }
        public List<RequirementDto>? Requirements { get; set; }
        public List<string>? Preserve { get; set; }
        public List<string>? Risks { get; set; }
        public List<string>? OpenQuestions { get; set; }

        public PlanAnalysis ToModel() => new()
        {
            Facts = (Facts ?? []).Where(f => !string.IsNullOrWhiteSpace(f.Text)).Select(f => new RepoFact(f.Text!.Trim(), Clean(f.Evidence))).ToList(),
            Assumptions = Clean(Assumptions),
            Requirements = (Requirements ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Text))
                .Select((r, i) => new Requirement(string.IsNullOrWhiteSpace(r.Id) ? $"REQ-{i + 1}" : r.Id.Trim(), r.Text!.Trim(), string.IsNullOrWhiteSpace(r.Kind) ? "functional" : r.Kind.Trim()))
                .ToList(),
            Preserve = Clean(Preserve),
            Risks = Clean(Risks),
            OpenQuestions = Clean(OpenQuestions),
        };
    }

    internal sealed class FactDto
    {
        public string? Text { get; set; }
        public List<string>? Evidence { get; set; }
    }

    internal sealed class RequirementDto
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
        public string? Kind { get; set; }
    }

    internal sealed class ArchitectureDto
    {
        public string? Summary { get; set; }
        public List<ComponentDto>? Components { get; set; }
        public List<string>? Invariants { get; set; }
        public string? ErrorHandling { get; set; }
        public string? Security { get; set; }
        public string? Performance { get; set; }

        public ArchitectureDesign ToModel() => new()
        {
            Summary = Summary?.Trim() ?? "",
            Components = (Components ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.Path))
                .Select(c => new ComponentDesign(c.Name?.Trim() ?? c.Path!.Trim(), c.Path?.Trim() ?? "", c.Change?.Trim().ToLowerInvariant() ?? "modify", c.Responsibility?.Trim() ?? ""))
                .ToList(),
            Invariants = Clean(Invariants),
            ErrorHandling = ErrorHandling?.Trim(),
            Security = Security?.Trim(),
            Performance = Performance?.Trim(),
        };
    }

    internal sealed class ComponentDto
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? Change { get; set; }
        public string? Responsibility { get; set; }
    }

    internal sealed class DecisionDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Decision { get; set; }
        public string? Rationale { get; set; }
        public List<string>? AlternativesRejected { get; set; }
        public List<string>? Constraints { get; set; }

        public ArchitectureDecision ToModel() => new()
        {
            Id = Id!.Trim(),
            Title = Title?.Trim() ?? Id!.Trim(),
            Decision = Decision?.Trim() ?? "",
            Rationale = Rationale?.Trim() ?? "",
            AlternativesRejected = Clean(AlternativesRejected),
            Constraints = Clean(Constraints),
        };
    }

    internal sealed class UnitDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Objective { get; set; }
        public string? Spec { get; set; }

        [JsonPropertyName("depends_on")]
        public List<string>? DependsOn { get; set; }

        /// <summary>An array of paths (original contract) or an object with create / modify / avoid (blueprint).</summary>
        public JsonElement? Files { get; set; }

        public List<string>? Create { get; set; }
        public List<string>? Modify { get; set; }
        public List<string>? Avoid { get; set; }
        public List<string>? References { get; set; }
        public List<string>? Steps { get; set; }
        public List<string>? ImplementationSteps { get; set; }
        public List<string>? Interfaces { get; set; }
        public List<string>? Decisions { get; set; }
        public List<string>? Constraints { get; set; }
        public List<string>? EdgeCases { get; set; }
        public List<string>? Requirements { get; set; }
        public List<string>? Acceptance { get; set; }
        public List<string>? AcceptanceCriteria { get; set; }
        public List<string>? Verification { get; set; }
        public string? Risk { get; set; }
        public List<string>? SideEffects { get; set; }
        public List<TestDto>? Tests { get; set; }

        public WorkUnit ToUnit()
        {
            var files = new List<string>();
            var create = Clean(Create);
            var modify = Clean(Modify);
            var avoid = Clean(Avoid);
            if (Files is { ValueKind: JsonValueKind.Array } array)
            {
                files.AddRange(Strings(array));
            }
            else if (Files is { ValueKind: JsonValueKind.Object } obj)
            {
                create.AddRange(Strings(obj, "create"));
                modify.AddRange(Strings(obj, "modify"));
                avoid.AddRange(Strings(obj, "avoid"));
            }

            create = create.Select(PlanPaths.Normalize).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            modify = modify.Select(PlanPaths.Normalize).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var steps = Clean(Steps?.Count > 0 ? Steps : ImplementationSteps);
            return new WorkUnit
            {
                Id = Id?.Trim() ?? "",
                Title = Title?.Trim() ?? Id ?? "",
                Spec = (Spec ?? Objective)?.Trim() ?? "",
                Objective = Objective?.Trim(),
                DependsOn = Clean(DependsOn),
                Files = Clean(files.Concat(create).Concat(modify)),
                Create = create,
                Modify = modify,
                Avoid = Clean(avoid),
                References = Clean(References),
                Steps = steps,
                Interfaces = Clean(Interfaces),
                Decisions = Clean(Decisions),
                Constraints = Clean(Constraints),
                EdgeCases = Clean(EdgeCases),
                Requirements = Clean(Requirements),
                Acceptance = Clean((Acceptance ?? []).Concat(AcceptanceCriteria ?? [])),
                Verification = Clean(Verification),
                Risk = ParseRisk(Risk),
                SideEffects = Clean(SideEffects),
                Tests = (Tests ?? []).Select(t => new AcceptanceTestFile((t.Path ?? "").Replace('\\', '/').Trim(), t.Content ?? "")).ToList(),
            };
        }

        private static IEnumerable<string> Strings(JsonElement array) =>
            array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!);

        private static IEnumerable<string> Strings(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? Strings(v) : [];
    }

    internal sealed class TestDto
    {
        public string? Path { get; set; }
        public string? Content { get; set; }
    }

    internal sealed class RuleDto
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
    }

    /// <summary>A plan operation as the architect writes it (see <see cref="PlanOps"/>).</summary>
    internal sealed class OperationDto
    {
        public string? Op { get; set; }
        public string? Unit { get; set; }
        public string? Spec { get; set; }
        public string? Objective { get; set; }
        public List<string>? Steps { get; set; }
        public bool? ReplaceSteps { get; set; }
        public List<string>? Constraints { get; set; }
        public List<string>? EdgeCases { get; set; }
        public List<string>? Acceptance { get; set; }
        public List<string>? Verification { get; set; }
        public List<string>? Create { get; set; }
        public List<string>? Modify { get; set; }
        public List<string>? Avoid { get; set; }
        public List<string>? References { get; set; }
        public List<string>? Interfaces { get; set; }
        public List<string>? Decisions { get; set; }
        public List<string>? Corrections { get; set; }

        [JsonPropertyName("depends_on")]
        public List<string>? DependsOn { get; set; }

        public string? Risk { get; set; }
        public UnitDto? NewUnit { get; set; }
        public List<UnitDto>? Parts { get; set; }
        public string? Text { get; set; }
        public string? Scope { get; set; }
        public List<string>? AppliesTo { get; set; }
        public string? ForbiddenPattern { get; set; }

        public PlanOperation ToModel() => new()
        {
            Op = Op?.Trim().ToLowerInvariant() ?? "",
            Unit = Unit?.Trim(),
            Spec = Spec,
            Objective = Objective,
            Steps = Clean(Steps),
            ReplaceSteps = ReplaceSteps == true,
            Constraints = Clean(Constraints),
            EdgeCases = Clean(EdgeCases),
            Acceptance = Clean(Acceptance),
            Verification = Clean(Verification),
            Create = Clean(Create),
            Modify = Clean(Modify),
            Avoid = Clean(Avoid),
            References = Clean(References),
            Interfaces = Clean(Interfaces),
            Decisions = Clean(Decisions),
            Corrections = Clean(Corrections),
            DependsOn = Clean(DependsOn),
            Risk = Risk is null ? null : ParseRisk(Risk),
            NewUnit = NewUnit?.ToUnit(),
            Parts = (Parts ?? []).Select(p => p.ToUnit()).ToList(),
            Text = Text,
            Scope = Scope,
            AppliesTo = Clean(AppliesTo),
            ForbiddenPattern = ForbiddenPattern,
        };
    }

    /// <summary>A lesson as the architect writes it in a supervision or review reply.</summary>
    internal sealed class LessonDto
    {
        public string? Text { get; set; }
        public string? Scope { get; set; }
        public List<string>? AppliesTo { get; set; }
        public string? ForbiddenPattern { get; set; }

        public PlanOperation ToOperation(string? sourceUnit) => new()
        {
            Op = PlanOps.AddLesson,
            Unit = sourceUnit,
            Text = Text,
            Scope = Scope,
            AppliesTo = Clean(AppliesTo),
            ForbiddenPattern = ForbiddenPattern,
        };
    }

    /// <summary>The vocabulary of plan operations, for prompts that let the architect revise the plan.</summary>
    public const string OperationsHelp = """
        Plan operations (each an object in "plan_updates"; only pending units can change, completed units never):
        - {"op":"update_unit","unit":"U3", ...fields}  fields: "spec", "objective" (replace); "steps" (appended, or
          replaced with "replace_steps": true); "constraints", "edge_cases", "acceptance", "verification", "create",
          "modify", "avoid", "references", "interfaces", "decisions", "corrections", "depends_on" (appended); "risk".
        - {"op":"add_unit","new_unit":{unit object like in the plan}}  (no tests)
        - {"op":"remove_unit","unit":"U5","text":"why it is unnecessary"}
        - {"op":"split_unit","unit":"U3","parts":[{unit},{unit}]}  (2-5 parts; the original's tests and acceptance move to the last part)
        - {"op":"add_dependency","unit":"U4","depends_on":["U3"]} / {"op":"remove_dependency", ...}
        - {"op":"add_dont","text":"..."} / {"op":"add_rule","text":"..."}
        Lessons (in "lessons"): {"text":"what every later unit must know","scope":"global"|"units","applies_to":["U4"],
          "forbidden_pattern":"optional regex no later added line may match (only when the mistake has a reliable textual signature)"}
        Judge each lesson's scope: global only when it truly applies to all remaining work.
        """;
}
