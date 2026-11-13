using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Llm.Architect;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Ledger;

namespace Tokonomy.Runtime.Orchestration;

public sealed record PlannedTask(Contract Contract, decimal ArchitectUsd, bool CostEstimated)
{
    /// <summary>The plan's engineering-quality verdict (always Ready for strategies without an architect plan).</summary>
    public PlanAssessment Assessment { get; init; } = new(PlanReadiness.Ready, []);

    public PlanCritique? Critique { get; init; }
    public int RevisionRounds { get; init; }
    public IReadOnlyList<string> InspectedFiles { get; init; } = [];

    /// <summary>Planning cost split by purpose (planning, inspection, critique, revision).</summary>
    public IReadOnlyDictionary<string, decimal> CostByPurpose { get; init; } = new Dictionary<string, decimal>();
}

/// <summary>Produces a contract for a strategy and records architect spend in the ledger.</summary>
public static class TaskPlanning
{
    /// <param name="policy">
    /// The project policy. When given, planning follows its <c>planning</c> section (blueprint, critique, revisions,
    /// file inspection) and its budget caps the extra rounds. When null, the original single-contract planning is used.
    /// </param>
    public static async Task<PlannedTask> PlanAsync(
        Strategy strategy, string task, string repoRoot, string baseSha, string runId,
        IProvider? provider, string architectModel, ILedger ledger, GitCli git, CancellationToken ct,
        AcceptanceConfig? acceptance = null, Clarifier? clarifier = null, Policy? policy = null, Action<string>? onStage = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(ledger);
        if (!strategy.UsesArchitectPlan)
        {
            return new PlannedTask(Strategy.SingleUnit(task), 0, false);
        }

        if (provider is null)
        {
            throw new InvalidOperationException("This strategy needs the architect provider.");
        }

        onStage?.Invoke("reading the repository");
        var inventory = await RepositoryAnalyzer.AnalyzeAsync(repoRoot, baseSha, task, git, ct, policy?.Planning.MaxContextChars ?? 60_000);
        PlanningOptions? options = null;
        if (policy is not null)
        {
            var p = policy.Planning;
            options = new PlanningOptions
            {
                Blueprint = p.Blueprint,
                Critique = p.Critique,
                MaxRevisionRounds = Math.Max(0, p.MaxRevisionRounds),
                RepoFiles = inventory.Files,
                ProtectedPaths = policy.Escalation.ProtectedPaths,
                ReadFiles = p.InspectFiles ? (paths, token) => RepositoryAnalyzer.ReadFilesAsync(repoRoot, baseSha, inventory.Files, paths, git, token) : null,
                MaxInspectFiles = p.MaxInspectFiles,
                // Extra rounds stop once planning has used half the task budget: the work itself still has to be paid for.
                SpendCapUsd = policy.Budget.MaxUsdPerTask / 2,
                OnStage = onStage,
            };
        }

        var planner = new Planner(Tokonomy.Runtime.Providers.GuardedProvider.Wrap(provider)!, architectModel);
        try
        {
            var result = await planner.PlanAsync(task, inventory.Context, ct, acceptance, clarifier, options);
            var (usd, estimated, byPurpose) = Record(ledger, runId, result.Calls, result.Purposes);
            return new PlannedTask(result.Contract, usd, estimated)
            {
                Assessment = result.Assessment,
                Critique = result.Critique,
                RevisionRounds = result.RevisionRounds,
                InspectedFiles = result.InspectedFiles,
                CostByPurpose = byPurpose,
            };
        }
        catch (PlanningException ex)
        {
            Record(ledger, runId, ex.Calls, ex.Purposes);
            throw;
        }
    }

    private static (decimal Usd, bool Estimated, Dictionary<string, decimal> ByPurpose) Record(
        ILedger ledger, string runId, IReadOnlyList<ProviderResponse> calls, IReadOnlyList<string> purposes)
    {
        decimal total = 0;
        var estimated = false;
        var byPurpose = new Dictionary<string, decimal>(StringComparer.Ordinal);
        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var purpose = i < purposes.Count ? purposes[i] : PlanPurposes.Planning;
            estimated |= call.Usd is null || call.Estimated;
            total += call.Usd ?? 0;
            byPurpose[purpose] = byPurpose.GetValueOrDefault(purpose) + (call.Usd ?? 0);
            ledger.RecordLlmCall(new LlmCallRecord
            {
                RunId = runId,
                Role = LlmRole.Architect,
                Model = call.Model,
                InputTokens = call.InputTokens,
                OutputTokens = call.OutputTokens,
                CachedTokens = call.CachedTokens,
                Usd = call.Usd ?? 0,
                Source = call.Usd is null || call.Estimated ? CostSource.Estimated : CostSource.Metered,
                Purpose = purpose,
            });
        }

        return (total, estimated, byPurpose);
    }
}
