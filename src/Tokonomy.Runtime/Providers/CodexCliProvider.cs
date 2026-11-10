using System.Text;
using Tokonomy.Core.Net;
using Tokonomy.Core.Abstractions;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Providers;

/// <summary>
/// Architect calls through the OpenAI Codex CLI on its own login (a ChatGPT plan, or an API key given to <c>codex login</c>):
/// <c>codex exec</c> in an empty scratch folder with a read-only sandbox, the whole prompt on stdin, and the final message
/// written to a file. Provider keys are scrubbed from the child environment, so the login Codex holds decides who is billed.
/// Tokens come from the JSON event stream; cost is never reported, so it is recorded as estimated.
/// </summary>
public sealed class CodexCliProvider(IProcessRunner runner, string command = "codex", TimeSpan? timeout = null) : IProvider
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workDir = Path.Combine(Path.GetTempPath(), "tokonomy-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var answerFile = Path.Combine(workDir, "answer.txt");
            var args = new List<string>
            {
                command, "exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--json", "--color", "never",
                "--output-last-message", answerFile,
            };
            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                args.AddRange(["--model", request.Model]);
            }

            args.Add("-");

            var prompt = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(request.System))
            {
                prompt.AppendLine(request.System.Trim()).AppendLine().AppendLine("---").AppendLine();
            }

            prompt.AppendLine(request.User.Trim());
            if (request.JsonOutput)
            {
                prompt.AppendLine().AppendLine("Answer with a single JSON object only, without a code fence. Do not run commands.");
            }

            var outcome = await runner.RunAsync(new ProcessSpec
            {
                Args = args,
                WorkingDirectory = workDir,
                Environment = EnvironmentBuilder.ForGate([], null, ProxySettings.Current.ForProvider("codex")),
                ReplaceEnvironment = true,
                Timeout = _timeout,
                Proxy = ProxySettings.Current.ForProvider("codex"),
                StdinText = prompt.ToString(),
            }, ct);

            if (outcome.TimedOut)
            {
                throw new InvalidOperationException($"Codex did not answer within {_timeout.TotalMinutes:0} minutes.");
            }

            var fromFile = File.Exists(answerFile) ? await File.ReadAllTextAsync(answerFile, CancellationToken.None) : "";
            return Parse(outcome.ExitCode, outcome.Stdout, outcome.Stderr, fromFile, request.Model);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static ProviderResponse Parse(int exitCode, string stdout, string stderr, string answerFileText, string requestedModel)
    {
        var run = CodexRun.Parse(stdout);
        var text = (answerFileText.Trim().Length > 0 ? answerFileText : run.Message).Trim();
        if (exitCode != 0 || run.Error is not null || text.Length == 0)
        {
            var why = run.Error ?? (text.Length > 0 ? text : Tail(stderr + stdout));
            throw new InvalidOperationException($"Codex failed (exit {exitCode}): {Tail(why)}");
        }

        return new ProviderResponse(text, requestedModel, run.InputTokens, run.OutputTokens, run.CachedTokens, null, Estimated: true);
    }

    private static string Tail(string text) => text.Length <= 400 ? text.Trim() : "..." + text[^400..].Trim();
}
