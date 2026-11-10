using System.Text;
using System.Text.RegularExpressions;
using Tokonomy.Core.Model;

namespace Tokonomy.Llm.Architect;

/// <summary>
/// Prompt and parser for the final review. The architect sees the task, the rules, what each unit went through and the
/// combined diff; it answers with a verdict line, a one-sentence summary and, only if needed, a short list of concerns.
/// </summary>
public static partial class ReviewPrompt
{
    public const int MaxDiffChars = 60_000;

    public const string System = """
        You are the senior engineer doing the final review of a change that a junior engineer produced unit by unit.
        The automated checks already pass, so do not repeat them. Look only for things checks miss: the task is not
        actually satisfied, a project rule is broken, unrelated files were changed, or the code is risky or wrong in a
        way tests would not catch. Do not raise style nits.

        Answer in exactly this form:

        VERDICT: APPROVE
        SUMMARY: one sentence

        or

        VERDICT: CONCERNS
        SUMMARY: one sentence
        CONCERNS:
        - one concrete problem per line, naming the file
        """;

    public static string Build(Contract contract, string patch, IReadOnlyList<(string Id, string Title, string Status, int Attempts, string Reason)> units)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(units);

        var sb = new StringBuilder();
        sb.AppendLine("# Overall task").AppendLine(contract.Task.Trim()).AppendLine();
        sb.AppendLine("# Project rules");
        AppendList(sb, contract.Rules.Select(r => $"{r.Id}: {r.Text}"));
        sb.AppendLine("# Do not");
        AppendList(sb, contract.DontList);
        sb.AppendLine("# How each unit went");
        AppendList(sb, units.Select(u => $"{u.Id} {u.Title}: {u.Status} after {u.Attempts} attempt(s) ({u.Reason})"));
        sb.AppendLine("# Combined diff (all units)");
        var shown = patch.Length <= MaxDiffChars ? patch.TrimEnd() : patch[..MaxDiffChars] + "\n... (diff truncated)";
        sb.AppendLine(patch.Length == 0 ? "(empty)" : "```diff\n" + shown + "\n```");
        return sb.ToString();
    }

    public static FinalReview Parse(string response, string model)
    {
        ArgumentNullException.ThrowIfNull(response);
        var verdict = VerdictLine().Match(response);
        var summary = SummaryLine().Match(response) is { Success: true } s ? s.Groups[1].Value.Trim() : "";
        if (!verdict.Success)
        {
            return new FinalReview(ReviewVerdict.Unavailable, "the review reply had no VERDICT line", [], model);
        }

        var concerns = new List<string>();
        var header = ConcernsHeader().Match(response);
        if (header.Success)
        {
            foreach (var line in response[(header.Index + header.Length)..].Split('\n', StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    concerns.Add(line[2..].Trim());
                }
            }
        }

        var approve = verdict.Groups[1].Value.Equals("APPROVE", StringComparison.OrdinalIgnoreCase);
        // "CONCERNS" with nothing listed is still a concern; "APPROVE" with a list contradicts itself, so trust the list.
        return approve && concerns.Count == 0
            ? new FinalReview(ReviewVerdict.Approve, summary, [], model)
            : new FinalReview(ReviewVerdict.Concerns, summary, concerns, model);
    }

    private static void AppendList(StringBuilder sb, IEnumerable<string> items)
    {
        var any = false;
        foreach (var item in items)
        {
            sb.AppendLine($"- {item}");
            any = true;
        }

        if (!any)
        {
            sb.AppendLine("(none)");
        }

        sb.AppendLine();
    }

    [GeneratedRegex(@"^VERDICT:\s*(APPROVE|CONCERNS)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex VerdictLine();

    [GeneratedRegex(@"^SUMMARY:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"^CONCERNS:\s*$", RegexOptions.Multiline)]
    private static partial Regex ConcernsHeader();
}
