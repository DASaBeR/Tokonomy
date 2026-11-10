using System.Text.RegularExpressions;

namespace Tokonomy.Runtime.Ledger;

/// <summary>Removes credential-shaped strings before anything is written to transcripts or run artifacts.</summary>
public static partial class Redactor
{
    public const string Mask = "[REDACTED]";

    public static string Redact(string text, IEnumerable<string>? knownSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var secret in knownSecrets ?? [])
        {
            if (secret.Length >= 8)
            {
                text = text.Replace(secret, Mask, StringComparison.Ordinal);
            }
        }

        text = KeyPatterns().Replace(text, Mask);
        text = JsonCredentialField().Replace(text, "$1" + Mask);
        return BearerHeader().Replace(text, "$1" + Mask);
    }

    // Fields of OAuth credential files (e.g. ~/.gemini/oauth_creds.json, ~/.claude/.credentials.json) if one is printed.
    [GeneratedRegex(@"(""(?:access_token|refresh_token|id_token|client_secret|accessToken|refreshToken)""\s*:\s*"")[^""]+", RegexOptions.IgnoreCase)]
    private static partial Regex JsonCredentialField();

    // OpenAI/OpenRouter/Anthropic (sk-...), Google API keys (AIza...) and OAuth access/refresh tokens (ya29., 1//0),
    // GitHub (ghp_, github_pat_), AWS access keys, Slack.
    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{16,}|AIza[0-9A-Za-z_\-]{30,}|ya29\.[0-9A-Za-z_\-]{20,}|1//0[0-9A-Za-z_\-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9\-]{10,})")]
    private static partial Regex KeyPatterns();

    [GeneratedRegex(@"((?:Authorization|x-api-key)\s*[:=]\s*(?:Bearer\s+)?)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerHeader();
}
