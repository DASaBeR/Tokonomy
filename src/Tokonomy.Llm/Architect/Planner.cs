using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;

namespace Tokonomy.Llm.Architect;

public sealed class PlanningException(string message, IReadOnlyList<ProviderResponse> calls) : Exception(message)
{
    public IReadOnlyList<ProviderResponse> Calls { get; } = calls;
}

public sealed record PlanResult(Contract Contract, IReadOnlyList<ProviderResponse> Calls);

/// <summary>
/// Asks the architect for a contract as JSON, validates its shape and DAG, and re-asks once with the validation
/// errors. Every call is returned so the caller can record it in the ledger, even on failure.
/// </summary>
public sealed partial class Planner(IProvider provider, string model)
{
    internal const string SystemPrompt = """
        You are the architect in a cost-sensitive coding pipeline. A cheaper model will implement your plan one unit
        at a time, in order, and each unit is verified by deterministic gates (build, lint, tests). Write a contract.

        Guidelines:
        - Prefer few, small, independently verifiable units (1-6). Each unit should be a coherent change a junior
          engineer could finish in one sitting, ideally under 200 changed lines.
        - Each spec must be concrete: what to change, where, and the expected behavior. Name files when you can.
        - Order units so dependencies come first; list them in depends_on.
        - Rules are project constraints the implementation must respect (architecture, style, safety). Keep them short.
        - The don't list names tempting mistakes to avoid.
        - Acceptance items describe observable checks (tests that should pass, behavior to verify).

        Respond with ONLY a JSON object, no prose, matching (omit "tests" unless the section below requires them):
        {
          "units": [
            {"id": "U1", "title": "...", "spec": "...", "depends_on": [], "files": ["path"], "acceptance": ["..."]}
          ],
          "rules": [{"id": "R1", "text": "..."}],
          "dont": ["..."]
        }
        Unit ids: a letter followed by letters, digits, '-' or '_' (e.g. U1).
        """;

    internal static string AcceptancePrompt(AcceptanceConfig a) => $$"""


        Executable acceptance tests (REQUIRED for this project):
        - Write the tests yourself. Each unit gets "tests": [{"path": "{{a.Dir}}/test_xxx.py", "content": "<full file text>"}].
          The tests for unit N run after unit N and every earlier unit, so a unit's tests may only assert what units up to
          and including it deliver. Never assert something a later unit builds.
        - Paths must be inside "{{a.Dir}}/". The worker cannot edit these files and the unit is rejected until the command
          `{{string.Join(' ', a.Cmd)}}` passes, so they are the real definition of done: make them strict enough to catch a
          lazy or empty implementation, and fair enough that a correct implementation of the spec passes.
        - Tests run in an isolated environment without network, using only what the command already has. Use the
          standard library or tools already in the repository. Do not require installing packages.
        - Test observable behavior and structure (files exist, content, parsing, outputs), not implementation details.
        - The worker sees the tests, so keep the spec and the tests consistent. Name every file the tests read.
        {{(string.IsNullOrWhiteSpace(a.Hint) ? "" : "- Project hint: " + a.Hint.Trim())}}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task<PlanResult> PlanAsync(string task, string repoContext, CancellationToken ct, AcceptanceConfig? acceptance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        var acceptanceOn = acceptance is { Enabled: true };
        var system = acceptanceOn ? SystemPrompt + AcceptancePrompt(acceptance!) : SystemPrompt;
        var calls = new List<ProviderResponse>();
        var user = $"""
            # Task
            {task.Trim()}

            # Repository context
            {repoContext}
            """;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await provider.CompleteAsync(new ProviderRequest(model, system, user, JsonOutput: true), ct);
            calls.Add(response);

            var (contract, errors) = Parse(task, response.Text, acceptanceOn ? acceptance!.Dir : null);
            if (contract is not null && errors.Count == 0)
            {
                return new PlanResult(contract, calls);
            }

            if (attempt == 2)
            {
                throw new PlanningException("Architect returned an invalid contract twice: " + string.Join("; ", errors), calls);
            }

            user += $"""


                # Your previous answer was invalid
                {string.Join("\n", errors.Select(e => "- " + e))}

                Previous answer:
                {Truncate(response.Text, 6000)}

                Return a corrected JSON object only.
                """;
        }

        throw new InvalidOperationException("unreachable");
    }

    internal static (Contract? Contract, IReadOnlyList<string> Errors) Parse(string task, string text, string? acceptanceDir = null)
    {
        var json = ExtractJson(text);
        if (json is null)
        {
            return (null, ["response contained no JSON object"]);
        }

        ContractDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ContractDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return (null, [$"invalid JSON: {ex.Message}"]);
        }

        if (dto?.Units is null)
        {
            return (null, ["missing 'units' array"]);
        }

        var contract = new Contract
        {
            Task = task,
            Units = dto.Units.Select(u => new WorkUnit
            {
                Id = u.Id?.Trim() ?? "",
                Title = u.Title?.Trim() ?? u.Id ?? "",
                Spec = u.Spec?.Trim() ?? "",
                DependsOn = u.DependsOn ?? [],
                Files = u.Files ?? [],
                Acceptance = u.Acceptance ?? [],
                Tests = (u.Tests ?? []).Select(t => new AcceptanceTestFile((t.Path ?? "").Replace('\\', '/').Trim(), t.Content ?? "")).ToList(),
            }).ToList(),
            Rules = (dto.Rules ?? []).Select((r, i) => new Rule(string.IsNullOrWhiteSpace(r.Id) ? $"R{i + 1}" : r.Id.Trim(), r.Text?.Trim() ?? "")).ToList(),
            DontList = dto.Dont ?? [],
        };

        var problems = ContractValidator.Validate(contract).ToList();
        if (acceptanceDir is not null)
        {
            problems.AddRange(AcceptancePaths.Validate(contract, acceptanceDir));
        }

        return (contract, problems);
    }

    internal static string? ExtractJson(string text)
    {
        var fenced = FencedJson().Match(text);
        var candidate = fenced.Success ? fenced.Groups[1].Value : text;
        var start = candidate.IndexOf('{', StringComparison.Ordinal);
        var end = candidate.LastIndexOf('}');
        return start >= 0 && end > start ? candidate[start..(end + 1)] : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    [GeneratedRegex(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline)]
    private static partial Regex FencedJson();

    private sealed class ContractDto
    {
        public List<UnitDto>? Units { get; set; }
        public List<RuleDto>? Rules { get; set; }
        public List<string>? Dont { get; set; }
    }

    private sealed class UnitDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Spec { get; set; }

        [JsonPropertyName("depends_on")]
        public List<string>? DependsOn { get; set; }

        public List<string>? Files { get; set; }
        public List<string>? Acceptance { get; set; }
        public List<TestDto>? Tests { get; set; }
    }

    private sealed class TestDto
    {
        public string? Path { get; set; }
        public string? Content { get; set; }
    }

    private sealed class RuleDto
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
    }
}
