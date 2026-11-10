using Tokonomy.Core.Model;
using Tokonomy.Llm.Architect;

namespace Tokonomy.Runtime.Tests;

public class ReviewPromptTests
{
    [Fact]
    public void Parses_an_approval()
    {
        var review = ReviewPrompt.Parse("VERDICT: APPROVE\nSUMMARY: Looks right.\n", "claude-opus-5-5");

        Assert.Equal(ReviewVerdict.Approve, review.Verdict);
        Assert.Equal("Looks right.", review.Summary);
        Assert.Empty(review.Concerns);
        Assert.Equal("claude-opus-5-5", review.Model);
    }

    [Fact]
    public void Parses_concerns_as_a_list()
    {
        var review = ReviewPrompt.Parse("VERDICT: CONCERNS\nSUMMARY: Two problems.\nCONCERNS:\n- a.py: wrong rounding\n* b.py: unrelated edit\n", "m");

        Assert.Equal(ReviewVerdict.Concerns, review.Verdict);
        Assert.Equal(["a.py: wrong rounding", "b.py: unrelated edit"], review.Concerns);
    }

    [Fact]
    public void An_approval_that_lists_concerns_is_treated_as_concerns()
    {
        var review = ReviewPrompt.Parse("VERDICT: APPROVE\nSUMMARY: ok\nCONCERNS:\n- a.py: hmm\n", "m");
        Assert.Equal(ReviewVerdict.Concerns, review.Verdict);
    }

    [Fact]
    public void Concerns_with_nothing_listed_is_still_a_concern()
    {
        Assert.Equal(ReviewVerdict.Concerns, ReviewPrompt.Parse("VERDICT: CONCERNS\nSUMMARY: vague\n", "m").Verdict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Looks good to me!")]
    public void A_reply_without_a_verdict_is_unavailable_not_an_approval(string reply) =>
        Assert.Equal(ReviewVerdict.Unavailable, ReviewPrompt.Parse(reply, "m").Verdict);

    [Fact]
    public void The_prompt_carries_the_task_rules_unit_outcomes_and_a_bounded_diff()
    {
        var contract = new Contract
        {
            Task = "Add a feature",
            Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "s" }],
            Rules = [new Rule("R1", "keep it small")],
        };
        var huge = new string('x', ReviewPrompt.MaxDiffChars + 5000);

        var prompt = ReviewPrompt.Build(contract, huge, [("U1", "Feature", "accepted", 2, "all gates passed")]);

        Assert.Contains("Add a feature", prompt, StringComparison.Ordinal);
        Assert.Contains("R1: keep it small", prompt, StringComparison.Ordinal);
        Assert.Contains("U1 Feature: accepted after 2 attempt(s)", prompt, StringComparison.Ordinal);
        Assert.Contains("diff truncated", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length < ReviewPrompt.MaxDiffChars + 2000);
    }
}
