using Tokonomy.Core.Model;
using Tokonomy.Llm.Architect;

namespace Tokonomy.Runtime.Orchestration;

/// <summary>
/// The architect token-share cap (<c>budget.max_architect_token_share</c>). Judged only on token counts the providers
/// reported, never on cost estimates: if any worker call reported no tokens, the share is not measurable and the cap is
/// not applied (the run records that once instead).
/// </summary>
public static class ArchitectShare
{
    /// <summary>Ledger event: an architect call was skipped, warned about, or the run was asked to stop.</summary>
    public const string CapEvent = "architect_share_cap";

    /// <summary>Ledger event: the cap could not be applied because worker token usage was not reported.</summary>
    public const string UnmeasurableEvent = "architect_share_unmeasurable";

    /// <summary>Planning happens before any worker runs; counting it would put every run at a 100% share from the start.</summary>
    public static readonly IReadOnlyCollection<string> ExcludedPurposes =
        [PlanPurposes.Planning, PlanPurposes.Inspection, PlanPurposes.Critique, PlanPurposes.Revision];

    public enum Verdict
    {
        /// <summary>Under the cap, or no worker usage yet to compare with.</summary>
        Allow,

        /// <summary>Some worker calls reported no tokens, so the share would be overstated; the cap is not applied.</summary>
        NotMeasurable,

        /// <summary>The architect's share of reported tokens is above the cap.</summary>
        Exceeded,
    }

    public static (Verdict Verdict, double? Share) Evaluate(TokenUse use, double cap)
    {
        ArgumentNullException.ThrowIfNull(use);
        if (use.WorkerCalls == 0)
        {
            return (Verdict.Allow, null);
        }

        if (use.WorkerCallsWithoutTokens > 0)
        {
            return (Verdict.NotMeasurable, null);
        }

        var total = use.ArchitectTokens + use.WorkerTokens;
        var share = total == 0 ? 0 : (double)use.ArchitectTokens / total;
        return (share > cap ? Verdict.Exceeded : Verdict.Allow, share);
    }
}

/// <summary>
/// Raised instead of making an architect call the token-share cap refused. It is an <see cref="InvalidOperationException"/>,
/// which every architect call site already treats as "architect unavailable": optional calls (unit review, final review)
/// are skipped, and a unit that needs the architect (takeover, supervision, split) stops instead of being bypassed.
/// </summary>
public sealed class ArchitectShareCapException(string message) : InvalidOperationException(message);
