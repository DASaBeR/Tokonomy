namespace Tokonomy.Core.Planning;

/// <summary>
/// A question the architect needs answered before it can write a good plan. Options may be empty (free text only).
/// <see cref="Recommended"/> holds the architect's picks, always taken from <see cref="Options"/>: exactly one for a
/// single-choice question, one or more for a checklist (<see cref="Multi"/>), where the user may tick several options.
/// </summary>
public sealed record ClarifyingQuestion(
    string Id, string Text, IReadOnlyList<string> Options, IReadOnlyList<string> Recommended, bool Multi = false);

public sealed record ClarifyingAnswer(string QuestionId, string Question, string Answer);

/// <summary>
/// Asks the user the architect's questions. Returns one answer per question, or null when the user chose to skip
/// (the architect then plans with sensible assumptions).
/// </summary>
public delegate Task<IReadOnlyList<ClarifyingAnswer>?> Clarifier(IReadOnlyList<ClarifyingQuestion> questions, CancellationToken ct);
