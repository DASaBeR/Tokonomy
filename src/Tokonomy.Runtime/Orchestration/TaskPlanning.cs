using System.Text;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Model;
using Tokonomy.Llm.Architect;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Ledger;

namespace Tokonomy.Runtime.Orchestration;

public sealed record PlannedTask(Contract Contract, decimal ArchitectUsd, bool CostEstimated);

/// <summary>Produces a contract for a strategy and records architect spend in the ledger.</summary>
public static class TaskPlanning
{
    private const int MaxListedFiles = 400;
    private const int MaxReadmeChars = 4000;

    public static async Task<PlannedTask> PlanAsync(
        Strategy strategy, string task, string repoRoot, string baseSha, string runId,
        IProvider? provider, string architectModel, ILedger ledger, GitCli git, CancellationToken ct,
        Tokonomy.Core.Config.AcceptanceConfig? acceptance = null)
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

        var context = await RepoContextAsync(repoRoot, baseSha, git, ct);
        var planner = new Planner(provider, architectModel);
        try
        {
            var result = await planner.PlanAsync(task, context, ct, acceptance);
            var (usd, estimated) = Record(ledger, runId, result.Calls);
            return new PlannedTask(result.Contract, usd, estimated);
        }
        catch (PlanningException ex)
        {
            Record(ledger, runId, ex.Calls);
            throw;
        }
    }

    internal static async Task<string> RepoContextAsync(string repoRoot, string baseSha, GitCli git, CancellationToken ct)
    {
        var files = (await git.RunAsync(repoRoot, ["ls-tree", "-r", "--name-only", baseSha], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new StringBuilder();
        sb.AppendLine($"## Files ({files.Length} tracked{(files.Length > MaxListedFiles ? $", first {MaxListedFiles} shown" : "")})");
        foreach (var f in files.Take(MaxListedFiles))
        {
            sb.AppendLine(f);
        }

        var readme = files.FirstOrDefault(f => f.Equals("README.md", StringComparison.OrdinalIgnoreCase) || f.Equals("README", StringComparison.OrdinalIgnoreCase));
        if (readme is not null && await git.ShowFileAsync(repoRoot, baseSha, readme, ct) is { } text)
        {
            sb.AppendLine();
            sb.AppendLine($"## {readme}");
            sb.AppendLine(text.Length <= MaxReadmeChars ? text : text[..MaxReadmeChars] + "\n...");
        }

        return sb.ToString();
    }

    private static (decimal Usd, bool Estimated) Record(ILedger ledger, string runId, IEnumerable<ProviderResponse> calls)
    {
        decimal total = 0;
        var estimated = false;
        foreach (var call in calls)
        {
            estimated |= call.Usd is null || call.Estimated;
            total += call.Usd ?? 0;
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
            });
        }

        return (total, estimated);
    }
}
