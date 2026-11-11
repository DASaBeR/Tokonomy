using Spectre.Console;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Planning;

namespace Tokonomy.Cli.Infrastructure;

/// <summary>Asks the user the architect's clarifying questions: pick an option, type an answer, or let the architect decide.</summary>
internal static class ClarifyPrompt
{
    private const string Own = "Type my own answer…";
    private const string Decide = "You decide";

    public static Clarifier For(IAnsiConsole console) => (questions, _) => Task.FromResult(Ask(console, questions));

    public static IReadOnlyList<ClarifyingAnswer>? Ask(IAnsiConsole console, IReadOnlyList<ClarifyingQuestion> questions)
    {
        Ui.DiscardPendingInput();
        console.WriteLine();
        console.MarkupLine($"[bold]The architect has {questions.Count} question{(questions.Count == 1 ? "" : "s")} before planning[/] [grey](so it does not have to guess)[/]");
        console.WriteLine();

        var answers = new List<ClarifyingAnswer>();
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var title = $"[bold]{i + 1}/{questions.Count}[/] {Markup.Escape(q.Text)}";
            string Label(string c) => q.Recommended.Contains(c) ? $"{Markup.Escape(c)} [green](Recommended)[/]" : Markup.Escape(c);

            var answer = q.Multi ? AskMany(console, q, title, Label) : AskOne(console, q, title, Label);
            answers.Add(new ClarifyingAnswer(q.Id, q.Text, answer.Length > 0 ? answer : "No preference - choose the most sensible option and say so in the spec."));
        }

        console.WriteLine();
        return answers;
    }

    /// <summary>One answer. The recommended option goes first, so Enter accepts it.</summary>
    private static string AskOne(IAnsiConsole console, ClarifyingQuestion q, string title, Func<string, string> label)
    {
        var ordered = q.Options.Where(q.Recommended.Contains).Concat(q.Options.Where(o => !q.Recommended.Contains(o)));
        var pick = console.Prompt(new SelectionPrompt<string>()
            .Title(title)
            .AddChoices(ordered.Append(Own).Append(Decide))
            .UseConverter(c => c == Decide ? $"[grey]{Decide}[/]" : c == Own ? Own : label(c)));

        return pick switch
        {
            Own => console.Prompt(new TextPrompt<string>("Your answer:").AllowEmpty()).Trim(),
            Decide => "",
            _ => pick,
        };
    }

    /// <summary>
    /// A checklist: the recommended options start ticked; Space toggles, Enter confirms. A free-text line always follows,
    /// so the user can add to the ticks or tick nothing and just type. Nothing ticked and nothing typed means "you decide".
    /// </summary>
    private static string AskMany(IAnsiConsole console, ClarifyingQuestion q, string title, Func<string, string> label)
    {
        var prompt = new MultiSelectionPrompt<string>()
            .Title(title)
            .NotRequired()
            .InstructionsText("[grey](Space to tick, Enter to confirm. You can type your own answer on the next line)[/]")
            .UseConverter(label);
        prompt.AddChoices(q.Options);
        foreach (var picked in q.Recommended)
        {
            prompt.Select(picked);
        }

        var chosen = console.Prompt(prompt).ToList();
        var note = console.Prompt(new TextPrompt<string>("[grey]Anything to add or change? Type it, or press Enter to skip:[/]").AllowEmpty()).Trim();
        if (note.Length > 0)
        {
            chosen.Add(note);
        }

        return string.Join("; ", chosen);
    }
}

/// <summary>
/// Shows a spinner around each architect call, so a question prompt between two calls is never inside a spinner. When the
/// provider streams its answer, the spinner also shows how much has been written so far.
/// </summary>
internal sealed class SpinningProvider(IProvider inner, IAnsiConsole console, string text) : IProvider
{
    public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        ProviderProgress? latest = null;
        return Ui.SpinAsync(console, text,
            () => inner.CompleteAsync(request with { OnProgress = p => Volatile.Write(ref latest, p) }, ct),
            () => Describe(Volatile.Read(ref latest)));
    }

    internal static string? Describe(ProviderProgress? p) => p switch
    {
        null => null,
        { Retrying: true, OutputChars: 0 } => "connection stalled, asking again",
        { Retrying: true } => $"asking again · ~{Tokens(p.ApproxTokens)} tokens written",
        _ => $"~{Tokens(p.ApproxTokens)} tokens written",
    };

    private static string Tokens(long n) => n >= 1000 ? (n / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
