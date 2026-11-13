using System.Security.Cryptography;
using System.Text;

namespace Tokonomy.Llm.Architect;

/// <summary>
/// Short content hashes of every architect prompt, so a benchmark records exactly which prompt versions produced its
/// numbers. Any wording change changes the hash.
/// </summary>
public static class PromptCatalog
{
    public static IReadOnlyDictionary<string, string> Hashes() => new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["planner.contract"] = Hash(Planner.SystemPrompt),
        ["planner.blueprint"] = Hash(Planner.BlueprintPrompt),
        ["planner.clarify"] = Hash(Planner.ClarifyPrompt),
        ["planner.inspect"] = Hash(Planner.InspectPrompt(12)),
        ["critic"] = Hash(PlanCritic.System),
        ["supervisor"] = Hash(SupervisorPrompt.System),
        ["unit_review"] = Hash(UnitReviewPrompt.System),
        ["decompose"] = Hash(DecomposePrompt.System),
        ["takeover"] = Hash(TakeoverPrompt.System),
        ["final_review"] = Hash(ReviewPrompt.System),
    };

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n"))))[..12];
}
