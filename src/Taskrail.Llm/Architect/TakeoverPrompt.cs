using System.Text;
using System.Text.RegularExpressions;
using Taskrail.Core.Model;
using Taskrail.Core.Takeover;

namespace Taskrail.Llm.Architect;

/// <summary>The architect's reply to a takeover request: the edits plus an optional note for the handoff.</summary>
public sealed record TakeoverReply(IReadOnlyList<EditBlock> Edits, string? Summary, string? Why);

/// <summary>
/// Prompt and parser for takeovers. The architect is not an agent: it sees the minimum context (contract, unit, the
/// worker's diff, the failure output and the current text of the touched files) and answers with search/replace edits
/// for the failing section only. The orchestrator applies them and re-runs the gates.
/// </summary>
public static partial class TakeoverPrompt
{
    public const int MaxFileChars = 24_000;
    public const int MaxFeedbackChars = 4_000;
    public const int MaxListedFiles = 150;

    public const string System = """
        You are the senior engineer on a team. A junior engineer's attempt at a work unit failed the automated checks.
        Fix it with the smallest possible change: do not rewrite working code and do not touch files unrelated to the
        failure. Respect the project rules and never edit locked regions.

        Output one or more edit blocks, exactly in this form:

        <<<<<<< SEARCH path/relative/to/repo.py
        exact lines currently in the file
        =======
        the replacement lines
        >>>>>>> REPLACE

        Rules: the SEARCH text must match the file exactly (including indentation) and must occur exactly once, so
        include enough surrounding lines to be unique. To create a new file use an empty SEARCH section. Use several
        blocks if needed.

        After the blocks, add two short lines for the engineer who continues after you:
        SUMMARY: what you changed, in one sentence
        WHY: what the junior got wrong, in one sentence
        """;

    public static string Build(
        Contract contract, WorkUnit unit, int attempts, string reason, string diff, string failureOutput,
        IReadOnlyDictionary<string, string> files, IReadOnlyList<string> repoFiles, IReadOnlyList<LockedRegion> locked, string? previousProblem = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(unit);
        var sb = new StringBuilder();
        sb.AppendLine("# Overall task").AppendLine(contract.Task.Trim()).AppendLine();
        sb.AppendLine("# Project rules");
        AppendList(sb, contract.Rules.Select(r => $"{r.Id}: {r.Text}"));
        sb.AppendLine("# Do not");
        AppendList(sb, contract.DontList);
        sb.AppendLine($"# Work unit {unit.Id}: {unit.Title}").AppendLine(unit.Spec.Trim()).AppendLine();
        if (unit.Acceptance.Count > 0)
        {
            sb.AppendLine("Acceptance:");
            AppendList(sb, unit.Acceptance);
        }

        sb.AppendLine($"# Why you were called: {reason}").AppendLine();
        sb.AppendLine($"# The junior's attempt (diff against the start of this unit; {attempts} attempt(s) so far)");
        sb.AppendLine(diff.Length == 0 ? "(no changes were made)" : "```diff\n" + diff.TrimEnd() + "\n```").AppendLine();
        sb.AppendLine("# Failure output from the checks");
        sb.AppendLine("```").AppendLine(Tail(failureOutput, MaxFeedbackChars)).AppendLine("```").AppendLine();

        if (locked.Count > 0)
        {
            sb.AppendLine("# Locked regions (written by an earlier takeover; never modify these lines)");
            foreach (var region in locked)
            {
                sb.AppendLine($"### {region.Path}").AppendLine("```").AppendLine(region.Text.Trim('\n')).AppendLine("```");
            }

            sb.AppendLine();
        }

        sb.AppendLine("# Current content of the relevant files");
        if (files.Count == 0)
        {
            sb.AppendLine("(none yet)");
        }

        foreach (var (path, content) in files)
        {
            var shown = content.Length <= MaxFileChars ? content : content[..MaxFileChars] + "\n... (truncated)";
            sb.AppendLine($"### {path}").AppendLine("```").AppendLine(shown.TrimEnd('\n')).AppendLine("```").AppendLine();
        }

        if (repoFiles.Count > 0)
        {
            sb.AppendLine("# Files in the repository");
            sb.AppendLine(string.Join('\n', repoFiles.Take(MaxListedFiles)));
            sb.AppendLine();
        }

        if (previousProblem is not null)
        {
            sb.AppendLine("# Your previous answer could not be applied").AppendLine(previousProblem).AppendLine();
            sb.AppendLine("Answer again with corrected edit blocks only.");
        }

        return sb.ToString();
    }

    public static TakeoverReply Parse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var edits = EditBlocks.Parse(response);
        var summary = SummaryLine().Match(response) is { Success: true } s ? s.Groups[1].Value.Trim() : null;
        var why = WhyLine().Match(response) is { Success: true } w ? w.Groups[1].Value.Trim() : null;
        return new TakeoverReply(edits, summary, why);
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

    private static string Tail(string text, int max) => text.Length <= max ? text.Trim() : "...\n" + text[^max..].Trim();

    [GeneratedRegex(@"^SUMMARY:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"^WHY:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex WhyLine();
}
