using System.Text.Json;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;

namespace Tokonomy.Llm.Architect;

public sealed class PlanningException(string message, IReadOnlyList<ProviderResponse> calls) : Exception(message)
{
    public IReadOnlyList<ProviderResponse> Calls { get; } = calls;

    /// <summary>What each call in <see cref="Calls"/> was for (see <see cref="PlanPurposes"/>).</summary>
    public IReadOnlyList<string> Purposes { get; init; } = [];
}

/// <summary>The plan and every architect call it took. <see cref="Purposes"/> lines up with <see cref="Calls"/>.</summary>
public sealed record PlanResult(Contract Contract, IReadOnlyList<ProviderResponse> Calls)
{
    public IReadOnlyList<string> Purposes { get; init; } = [];

    /// <summary>The deterministic quality verdict, plus the critique's findings when the plan was not revised after it.</summary>
    public PlanAssessment Assessment { get; init; } = new(PlanReadiness.Ready, []);

    public PlanCritique? Critique { get; init; }

    /// <summary>How many times the plan was revised after the quality check or the critique.</summary>
    public int RevisionRounds { get; init; }

    /// <summary>Files the architect asked to read before planning.</summary>
    public IReadOnlyList<string> InspectedFiles { get; init; } = [];
}

/// <summary>How thorough planning is. Null options keep the original single-contract behaviour.</summary>
public sealed record PlanningOptions
{
    /// <summary>Ask for an engineering blueprint (analysis, architecture, decisions, detailed units).</summary>
    public bool Blueprint { get; init; } = true;

    /// <summary>Run a separate critique call on the plan.</summary>
    public bool Critique { get; init; } = true;

    public int MaxRevisionRounds { get; init; } = 1;

    /// <summary>Tracked files at the base commit, for grounding checks. Null skips them.</summary>
    public IReadOnlySet<string>? RepoFiles { get; init; }

    public IReadOnlyList<string> ProtectedPaths { get; init; } = [];

    /// <summary>Reads repository files the architect asks for (read-only). Null: the architect cannot ask.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<string>>? ReadFiles { get; init; }

    public int MaxInspectFiles { get; init; } = 12;

    /// <summary>Extra rounds (critique, revisions) stop once planning has cost this much.</summary>
    public decimal? SpendCapUsd { get; init; }

    /// <summary>Progress messages ("checking the plan", "revising the plan"), for the CLI.</summary>
    public Action<string>? OnStage { get; init; }
}

/// <summary>Why an architect call was made while planning; recorded with its cost.</summary>
public static class PlanPurposes
{
    public const string Planning = "planning";
    public const string Inspection = "inspection";
    public const string Critique = "critique";
    public const string Revision = "revision";
}

/// <summary>
/// Asks the architect for a contract as JSON, validates its shape and DAG, and re-asks once with the validation
/// errors. With <see cref="PlanningOptions"/> it asks for an engineering blueprint, lets the architect read the files it
/// needs once, checks the plan's engineering quality deterministically, has a separate call critique it and revises it
/// (bounded). Every call is returned so the caller can record it in the ledger, even on failure.
/// </summary>
public sealed class Planner(IProvider provider, string model)
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

    internal const string BlueprintPrompt = """
        You are the principal engineer (architect) in a cost-sensitive coding pipeline. A cheaper model (the worker) will
        implement your plan one unit at a time, in order. The worker is told to follow your instructions exactly and NOT
        to make design decisions; deterministic gates (build, lint, tests) verify every unit. So do the engineering
        thinking now: understand the repository, decide the architecture, and write instructions precise enough that the
        worker never has to invent a strategy.

        Work in this order:
        1. Analysis. State verified facts about the repository, each with the file(s) that prove it. Evidence must be
           real paths from the repository context. Never invent files, classes, methods or dependencies: anything you
           could not verify goes under "assumptions". List the requirements with ids (REQ-1, ...; kind: functional,
           non_functional, edge_case, security, performance or compatibility), behaviour to preserve, and risks.
        2. Architecture. Summarize the design: components to create or modify (path and responsibility), invariants,
           error handling, and security or performance notes where they matter.
        3. Decisions. Record every important technical decision as an ADR (ADR-1, ...): the decision, why, the
           alternatives rejected, and the constraints it imposes. Anything the worker could reasonably decide
           differently must be decided here.
        4. Units. Prefer few, small, independently verifiable units (1-6), each under about 200 changed lines and with
           one responsibility. For each unit: objective; files {create, modify, avoid}; references to existing code to
           reuse; implementation steps in execution order (concrete: what to write where, never just "implement X");
           interfaces and signatures exactly as they must appear; the ADR ids and constraints that apply; edge cases;
           the requirements it covers (every requirement must be covered by some unit); measurable acceptance criteria;
           verification (how the worker can check its work, such as a test command); risk (low, medium or high; high
           means security, data, public contracts or wide impact).
        5. Rules: short project-wide constraints. Dont: tempting mistakes the worker must avoid.

        Only "modify" files that exist in the repository or that an earlier unit creates. Never plan changes under
        .tokonomy/. Do not add work the task does not ask for.

        Respond with ONLY a JSON object, no prose (omit empty optional fields; omit "tests" unless a section below
        requires them):
        {"schema_version": 2,
         "analysis": {"facts": [{"text": "...", "evidence": ["path"]}], "assumptions": ["..."],
           "requirements": [{"id": "REQ-1", "text": "...", "kind": "functional"}], "preserve": ["..."], "risks": ["..."]},
         "architecture": {"summary": "...", "components": [{"name": "...", "path": "...", "change": "create|modify", "responsibility": "..."}],
           "invariants": ["..."], "error_handling": "...", "security": "...", "performance": "..."},
         "decisions": [{"id": "ADR-1", "title": "...", "decision": "...", "rationale": "...", "alternatives_rejected": ["..."], "constraints": ["..."]}],
         "units": [{"id": "U1", "title": "...", "objective": "...", "spec": "short summary paragraph", "depends_on": [],
           "files": {"create": ["path"], "modify": ["path"], "avoid": ["path or dir/"]}, "references": ["path"],
           "steps": ["..."], "interfaces": ["..."], "decisions": ["ADR-1"], "constraints": ["..."], "edge_cases": ["..."],
           "requirements": ["REQ-1"], "acceptance": ["..."], "verification": ["..."], "risk": "low"}],
         "rules": [{"id": "R1", "text": "..."}],
         "dont": ["..."]}
        Unit ids: a letter followed by letters, digits, '-' or '_' (e.g. U1).
        """;

    internal static string InspectPrompt(int max) => $$"""


        Reading files (optional, once):
        - The repository context shows the file list but only some file contents. If you must read specific files to
          plan correctly (code you will modify, interfaces you will reuse), respond with ONLY
          {"read_files": ["path", ...]} (at most {{max}} tracked paths) instead of a plan. You get the contents once,
          then you must return the plan.
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

    internal const int MaxQuestions = 6;
    internal const int MaxQuestionRounds = 1;
    internal const int MaxInspectRounds = 1;

    internal const string ClarifyPrompt = """


        Clarifying questions (optional):
        - If the task is vague or leaves a decision open that would change the plan (stack, scope, behavior, where it
          goes), you may ask the user instead of guessing. Do this only when the answer matters; never ask what the
          repository already tells you, and never ask about trivia.
        - To ask, respond with ONLY this JSON object instead of a contract (at most 6 questions, 2-4 short options each;
          the user can always type their own answer):
          {"questions": [{"id": "Q1", "question": "...", "options": ["...", "..."], "recommended": "..."}]}
        - Every question MUST have a "recommended" value: the option you would pick yourself, copied exactly from its
          "options". Order questions by importance, most important first.
        - When the user should choose SEVERAL items from a list (for example which features to build first), add
          "multi": true, give up to 8 options, and make "recommended" an array of the options you would tick:
          {"id": "Q2", "question": "Which features first?", "options": ["A", "B", "C"], "multi": true, "recommended": ["A", "B"]}
        - You get one round of questions. Once you have the answers, respond with the contract.
        """;

    public async Task<PlanResult> PlanAsync(
        string task, string repoContext, CancellationToken ct, AcceptanceConfig? acceptance = null, Clarifier? clarifier = null,
        PlanningOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        var acceptanceOn = acceptance is { Enabled: true };
        var system = options is { Blueprint: true } ? BlueprintPrompt : SystemPrompt;
        if (acceptanceOn)
        {
            system += AcceptancePrompt(acceptance!);
        }

        var mayAsk = clarifier is not null;
        if (mayAsk)
        {
            system += ClarifyPrompt;
        }

        var mayRead = options?.ReadFiles is not null && options.MaxInspectFiles > 0;
        if (mayRead)
        {
            system += InspectPrompt(options!.MaxInspectFiles);
        }

        var calls = new List<ProviderResponse>();
        var purposes = new List<string>();
        var inspected = new List<string>();
        var user = $"""
            # Task
            {task.Trim()}

            # Repository context
            {repoContext}
            """;

        var rounds = 0;
        var reads = 0;
        Contract? contract = null;
        var planText = "";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await provider.CompleteAsync(new ProviderRequest(model, system, user, JsonOutput: true), ct);
            calls.Add(response);
            purposes.Add(PlanPurposes.Planning);

            if (mayAsk && rounds < MaxQuestionRounds && TryParseQuestions(response.Text) is { Count: > 0 } questions)
            {
                rounds++;
                attempt--; // asking is not a failed attempt
                var answers = await clarifier!(questions, ct);
                user += answers is { Count: > 0 }
                    ? $"""


                        # Answers to your questions
                        {string.Join("\n", answers.Select(a => $"- {a.Question} -> {a.Answer}"))}

                        Now return the contract JSON.
                        """
                    : """


                        # The user skipped your questions
                        Make reasonable assumptions, state them in the unit specs, and return the contract JSON.
                        """;
                continue;
            }

            if (mayRead && reads < MaxInspectRounds && TryParseReadRequest(response.Text) is { Count: > 0 } wanted)
            {
                reads++;
                attempt--; // reading is not a failed attempt
                purposes[^1] = PlanPurposes.Inspection;
                var paths = wanted.Take(options!.MaxInspectFiles).ToList();
                inspected.AddRange(paths);
                var contents = await options.ReadFiles!(paths, ct);
                user += $"""


                    # Files you asked to read (read-only, from the base commit)
                    {contents}

                    Now return the plan JSON.
                    """;
                continue;
            }

            var (parsed, errors) = Parse(task, response.Text, acceptanceOn ? acceptance!.Dir : null);
            if (parsed is not null && errors.Count == 0)
            {
                contract = parsed;
                planText = response.Text;
                break;
            }

            if (attempt == 2)
            {
                throw new PlanningException("Architect returned an invalid contract twice: " + string.Join("; ", errors), calls) { Purposes = purposes };
            }

            user += $"""


                # Your previous answer was invalid
                {string.Join("\n", errors.Select(e => "- " + e))}

                Previous answer:
                {Truncate(response.Text, 6000)}

                Return a corrected JSON object only.
                """;
        }

        if (contract is null)
        {
            throw new InvalidOperationException("unreachable");
        }

        if (options is null)
        {
            return new PlanResult(contract, calls) { Purposes = purposes, InspectedFiles = inspected };
        }

        var improved = await ImproveAsync(
            new Session(task, repoContext, system, user, acceptanceOn ? acceptance!.Dir : null, options, calls, purposes), contract, planText, ct);
        return improved with { InspectedFiles = inspected };
    }

    private sealed record Session(
        string Task, string RepoContext, string System, string User, string? AcceptanceDir, PlanningOptions Options,
        List<ProviderResponse> Calls, List<string> Purposes)
    {
        public bool CanSpend => Options.SpendCapUsd is not { } cap || Calls.Sum(c => c.Usd ?? 0) < cap;

        public PlanAssessment Assess(Contract c) => PlanQuality.Assess(c, Options.RepoFiles, Options.Blueprint, Options.ProtectedPaths);
    }

    /// <summary>
    /// Quality loop: the deterministic assessment, then (if enabled) a separate critique, with at most
    /// <see cref="PlanningOptions.MaxRevisionRounds"/> revisions in total. A revision that breaks the structure is
    /// discarded and the previous plan kept: revising must never make a plan worse.
    /// </summary>
    private async Task<PlanResult> ImproveAsync(Session s, Contract contract, string planText, CancellationToken ct)
    {
        s.Options.OnStage?.Invoke("checking the plan");
        var assessment = s.Assess(contract);
        PlanCritique? critique = null;
        var critiqueIsStale = false;
        var revisions = 0;
        while (revisions < s.Options.MaxRevisionRounds && s.CanSpend)
        {
            IReadOnlyList<string> problems;
            if (!assessment.IsReady)
            {
                problems = assessment.Blocking.Select(i => i.ToString()).ToList();
            }
            else if (s.Options.Critique && critique is null)
            {
                critique = await CritiqueAsync(s, contract, assessment, ct);
                if (critique.Accepts || !s.CanSpend)
                {
                    break;
                }

                problems = critique.Findings.Where(f => f.Severity is "high" or "medium").Select(f => f.ToString()).ToList();
            }
            else
            {
                break;
            }

            s.Options.OnStage?.Invoke("revising the plan");
            var revised = await ReviseAsync(s, planText, problems, ct);
            revisions++;
            if (revised is { } r)
            {
                contract = r.Contract;
                planText = r.Text;
                assessment = s.Assess(contract);
                critiqueIsStale = critique is not null;
            }
        }

        // A critique the plan was not revised after still tells the user what is weak (it never ran, or rounds ran out).
        if (s.Options.Critique && critique is null && s.CanSpend)
        {
            critique = await CritiqueAsync(s, contract, assessment, ct);
        }

        if (critique is { Verdict: CritiqueVerdict.Revise } && !critiqueIsStale)
        {
            var merged = assessment.Issues.Concat(critique.AsIssues()).ToList();
            var blocking = merged.Where(i => i.Blocking).ToList();
            assessment = new PlanAssessment(blocking.Count == 0 ? PlanReadiness.Ready : blocking.Min(i => i.Level), merged);
        }

        return new PlanResult(contract, s.Calls)
        {
            Purposes = s.Purposes,
            Assessment = assessment,
            Critique = critique,
            RevisionRounds = revisions,
        };
    }

    private async Task<PlanCritique> CritiqueAsync(Session s, Contract contract, PlanAssessment assessment, CancellationToken ct)
    {
        s.Options.OnStage?.Invoke("critiquing the plan");
        try
        {
            var prompt = PlanCritic.Build(s.Task, s.RepoContext, PlanJson.Describe(contract), assessment.Issues);
            var response = await provider.CompleteAsync(new ProviderRequest(model, PlanCritic.System, prompt, JsonOutput: true), ct);
            s.Calls.Add(response);
            s.Purposes.Add(PlanPurposes.Critique);
            return PlanCritic.Parse(response.Text);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.ClientModel.ClientResultException)
        {
            return new PlanCritique(CritiqueVerdict.Unavailable, $"the critique call failed: {ex.Message}", []);
        }
    }

    private async Task<(Contract Contract, string Text)?> ReviseAsync(Session s, string planText, IReadOnlyList<string> problems, CancellationToken ct)
    {
        var prompt = s.User + $"""


            # Your plan needs revision
            A review of your plan found these problems:
            {string.Join("\n", problems.Select(p => "- " + p))}

            Your plan:
            {Truncate(planText, 40_000)}

            Return the complete corrected plan as a JSON object only (all units, not just the changed ones). Fix every
            problem and keep what was right.
            """;
        var response = await provider.CompleteAsync(new ProviderRequest(model, s.System, prompt, JsonOutput: true), ct);
        s.Calls.Add(response);
        s.Purposes.Add(PlanPurposes.Revision);
        var (contract, errors) = Parse(s.Task, response.Text, s.AcceptanceDir);
        return contract is not null && errors.Count == 0 ? (contract, response.Text) : null;
    }

    internal static (Contract? Contract, IReadOnlyList<string> Errors) Parse(string task, string text, string? acceptanceDir = null)
    {
        var json = ExtractJson(text);
        if (json is null)
        {
            return (null, ["response contained no JSON object"]);
        }

        PlanJson.ContractDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PlanJson.ContractDto>(json, PlanJson.Options);
        }
        catch (JsonException ex)
        {
            return (null, [$"invalid JSON: {ex.Message}"]);
        }

        if (dto?.Units is null)
        {
            return (null, ["missing 'units' array"]);
        }

        var contract = PlanJson.ToContract(task, dto);
        var problems = ContractValidator.Validate(contract).ToList();
        if (acceptanceDir is not null)
        {
            problems.AddRange(AcceptancePaths.Validate(contract, acceptanceDir));
        }

        return (contract, problems);
    }

    /// <summary>The paths of a <c>{"read_files": [...]}</c> reply, else null.</summary>
    internal static IReadOnlyList<string>? TryParseReadRequest(string text)
    {
        var dto = PlanJson.Deserialize<PlanJson.ContractDto>(ExtractJson(text));
        return dto?.ReadFiles is { Count: > 0 } paths && dto.Units is not { Count: > 0 }
            ? paths.Select(PlanPaths.Normalize).OfType<string>().Distinct(StringComparer.Ordinal).ToList()
            : null;
    }

    /// <summary>Returns the architect's questions when the answer is a questions object (and not a contract), else null.</summary>
    internal static IReadOnlyList<ClarifyingQuestion>? TryParseQuestions(string text)
    {
        var json = ExtractJson(text);
        if (json is null)
        {
            return null;
        }

        QuestionsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<QuestionsDto>(json, PlanJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto?.Questions is not { Count: > 0 } || dto.Units is { Count: > 0 })
        {
            return null;
        }

        var list = new List<ClarifyingQuestion>();
        foreach (var q in dto.Questions.Take(MaxQuestions))
        {
            if (string.IsNullOrWhiteSpace(q.Question))
            {
                continue;
            }

            var multi = q.Multi == true;
            var options = (q.Options ?? []).Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).Distinct().Take(multi ? 8 : 5).ToList();
            multi &= options.Count >= 2;

            // Always end up with a recommendation: the model's picks that name real options, else the first option.
            var named = RecommendedNames(q.Recommended);
            var picks = options.Where(o => named.Contains(o, StringComparer.OrdinalIgnoreCase)).ToList();
            if (picks.Count == 0 && options.Count > 0)
            {
                picks.Add(options[0]);
            }

            list.Add(new ClarifyingQuestion(string.IsNullOrWhiteSpace(q.Id) ? $"Q{list.Count + 1}" : q.Id.Trim(), q.Question.Trim(), options,
                multi ? picks : picks.Take(1).ToList(), multi));
        }

        return list;
    }

    /// <summary>"recommended" may be one string or an array of strings.</summary>
    private static List<string> RecommendedNames(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } v => [v.GetString()!.Trim()],
        { ValueKind: JsonValueKind.Array } v => v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).ToList(),
        _ => [],
    };

    internal static string? ExtractJson(string text) => PlanJson.ExtractJson(text);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private sealed class QuestionsDto
    {
        public List<QuestionDto>? Questions { get; set; }
        public List<PlanJson.UnitDto>? Units { get; set; }
    }

    private sealed class QuestionDto
    {
        public string? Id { get; set; }
        public string? Question { get; set; }
        public List<string>? Options { get; set; }
        public JsonElement? Recommended { get; set; }
        public bool? Multi { get; set; }
    }
}
