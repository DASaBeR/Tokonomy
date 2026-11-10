using Spectre.Console.Testing;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Planning;

namespace Tokonomy.E2E.Tests;

public class ClarifyPromptTests
{
    private static TestConsole NewConsole() => new TestConsole().Interactive();

    private static string Answer(TestConsole console, params ClarifyingQuestion[] questions) =>
        ClarifyPrompt.Ask(console, questions)!.Single().Answer;

    [Fact]
    public void Single_choice_enter_accepts_the_recommended_option()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("B", Answer(console, new ClarifyingQuestion("Q1", "Which?", ["A", "B"], ["B"])));
    }

    [Fact]
    public void Single_choice_lets_the_user_type_their_own_answer()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.DownArrow); // past the two options
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);     // "Type my own answer…"
        console.Input.PushTextWithEnter("use Redis");

        Assert.Equal("use Redis", Answer(console, new ClarifyingQuestion("Q1", "Which?", ["A", "B"], ["A"])));
    }

    [Fact]
    public void A_question_without_options_is_still_answerable_by_typing()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Enter); // first choice is "Type my own answer…"
        console.Input.PushTextWithEnter("the admin panel");

        Assert.Equal("the admin panel", Answer(console, new ClarifyingQuestion("Q1", "Where?", [], [])));
    }

    [Fact]
    public void Checklist_enter_keeps_the_recommended_ticks()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Enter); // confirm ticks
        console.Input.PushKey(ConsoleKey.Enter); // skip the free-text line

        Assert.Equal("A", Answer(console, new ClarifyingQuestion("Q1", "Features?", ["A", "B", "C"], ["A"], Multi: true)));
    }

    [Fact]
    public void Checklist_can_be_answered_with_only_typed_text()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Spacebar); // untick the recommended "A"
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("none of these, start with billing");

        Assert.Equal("none of these, start with billing", Answer(console, new ClarifyingQuestion("Q1", "Features?", ["A", "B", "C"], ["A"], Multi: true)));
    }

    [Fact]
    public void Checklist_ticks_and_typed_text_are_combined()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Spacebar); // also tick "B"
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("and dark mode");

        Assert.Equal("A; B; and dark mode", Answer(console, new ClarifyingQuestion("Q1", "Features?", ["A", "B", "C"], ["A"], Multi: true)));
    }

    [Fact]
    public void Nothing_chosen_or_typed_means_the_architect_decides()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Spacebar); // untick "A"
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter);    // skip text

        Assert.Contains("No preference", Answer(console, new ClarifyingQuestion("Q1", "Features?", ["A", "B"], ["A"], Multi: true)), StringComparison.Ordinal);
    }
}
