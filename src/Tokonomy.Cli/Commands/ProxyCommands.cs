using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Net;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Process;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Cli.Commands;

public sealed class ProxyNoSettings : CommandSettings;

public sealed class ProxySetSettings : CommandSettings
{
    [CommandArgument(0, "<ADDRESS>")]
    [Description("Proxy address, e.g. 127.0.0.1:10808, socks5://127.0.0.1:10808 or http://127.0.0.1:10809. Without a scheme it is taken as http.")]
    public string Address { get; init; } = "";

    [CommandOption("--no-proxy <HOSTS>")]
    [Description("Comma-separated hosts that skip the proxy (localhost and 127.0.0.1 always do).")]
    public string? NoProxy { get; init; }

    [CommandOption("--only <IDS>")]
    [Description("Use the proxy only for these connections (comma-separated): claude, codex, antigravity, openrouter, anthropic, gemini, openai, deepseek, custom, other (git, tests and other tools). Or 'all'. Without it, a terminal is asked.")]
    public string? Only { get; init; }

    [CommandOption("--no-test")]
    [Description("Save without checking that the proxy answers.")]
    public bool NoTest { get; init; }
}

public sealed class ProxyScopeSettings : CommandSettings
{
    [CommandOption("--only <IDS>")]
    [Description("Use the proxy only for these connections (comma-separated), or 'all'. See `proxy set --help` for the ids. Without it, a terminal is asked.")]
    public string? Only { get; init; }
}

/// <summary>Which connections go through the proxy: asked as roles (strong, cheap, both) or provider by provider, stored as provider ids.</summary>
internal static class ProxyScopeUi
{
    public static readonly string[] KnownIds =
        [.. ProxyDiagnostics.AllTargets.Select(t => t.Id).Append(Sources.CustomId).Distinct(), ProxySettings.OtherId];

    /// <summary>Parses <c>claude,codex</c> or <c>all</c>; null when an id is unknown.</summary>
    public static List<string>? Parse(string text, out string error)
    {
        error = "";
        var ids = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(i => i.ToLowerInvariant()).Distinct().ToList();
        if (ids.Count == 0 || ids.Contains("all") || ids.Contains("everything"))
        {
            return [];
        }

        var unknown = ids.Where(i => !KnownIds.Contains(i)).ToList();
        if (unknown.Count > 0)
        {
            error = $"Unknown connection '{string.Join(", ", unknown)}'. Use: {string.Join(", ", KnownIds)} or all.";
            return null;
        }

        return ids;
    }

    private static string Title(string id) =>
        id == ProxySettings.OtherId ? "Git, tests and other tools"
            : id == Sources.CustomId ? "Custom provider"
            : ProxyDiagnostics.AllTargets.FirstOrDefault(t => t.Id == id)?.Title ?? id;

    /// <summary>Asks which traffic should use the proxy. Returns the provider ids, empty for everything.</summary>
    public static List<string> Ask(IAnsiConsole console, UserDefaults? defaults)
    {
        string strong = defaults?.Strong ?? "", cheap = defaults?.Cheap ?? "";
        var known = strong.Length > 0 && cheap.Length > 0;
        const string everything = "Everything (recommended)";
        const string choose = "Let me choose, connection by connection...";
        var options = new List<string> { everything };
        string? both = null, onlyStrong = null, onlyCheap = null;
        if (known)
        {
            if (strong.Equals(cheap, StringComparison.OrdinalIgnoreCase))
            {
                both = $"Both models: strong and cheap ({Title(strong)}), not git and tests";
                options.Add(both);
            }
            else
            {
                both = $"Both models: strong ({Title(strong)}) and cheap ({Title(cheap)}), not git and tests";
                onlyStrong = $"Only the strong model ({Title(strong)})";
                onlyCheap = $"Only the cheap model ({Title(cheap)})";
                options.AddRange([both, onlyStrong, onlyCheap]);
            }
        }

        options.Add(choose);
        console.WriteLine();
        var pick = console.Prompt(new SelectionPrompt<string>()
            .Title("Which models should use the proxy? [grey](the others connect directly)[/]")
            .AddChoices(options)
            .UseConverter(Markup.Escape));

        if (pick == everything)
        {
            return [];
        }

        if (pick == both)
        {
            return [strong, cheap];
        }

        if (pick == onlyStrong)
        {
            return [strong];
        }

        if (pick == onlyCheap)
        {
            return [cheap];
        }

        var targets = (defaults is null ? ProxyDiagnostics.AllTargets : ProxyDiagnostics.UsedTargets(defaults)).Select(t => t.Id).Distinct().ToList();
        targets.Add(ProxySettings.OtherId);
        string Role(string id) => id == strong && id == cheap ? "  [grey](strong and cheap)[/]" : id == strong ? "  [grey](your strong model)[/]" : id == cheap ? "  [grey](your cheap model)[/]" : "";
        var prompt = new MultiSelectionPrompt<string>()
            .Title("Use the proxy for: [grey](space to toggle, enter to confirm)[/]")
            .AddChoices(targets)
            .UseConverter(id => Markup.Escape(Title(id)) + Role(id));
        foreach (var id in targets)
        {
            prompt.Select(id);
        }

        var chosen = console.Prompt(prompt);
        return chosen.Count == 0 || chosen.Count == targets.Count ? [] : chosen.ToList();
    }
}

public sealed class ProxyTestSettings : CommandSettings
{
    [CommandOption("--all")]
    [Description("Check every known service, not only the ones this user set up.")]
    public bool All { get; init; }

    [CommandOption("--live")]
    [Description("Also make a tiny real call to each model you use (costs a few tokens per model).")]
    public bool Live { get; init; }
}

/// <summary>Everything the proxy commands share: one setting per user, saved in the user's profile.</summary>
internal static class ProxyStore
{
    public static void Use(ProxySettings settings)
    {
        settings.Save();
        ProxySettings.Current = ProxySettings.LoadUser();
    }
}

/// <summary><c>tokonomy proxy</c>: what is set now.</summary>
public sealed class ProxyShowCommand(IAnsiConsole console) : Command<ProxyNoSettings>
{
    public override int Execute(CommandContext context, ProxyNoSettings settings, CancellationToken cancellationToken)
    {
        var proxy = ProxySettings.Current;
        if (proxy.IsActive)
        {
            Ui.Done(console, $"Proxy is ON: {proxy.Display}" + (proxy.IsSocks ? " (SOCKS5; tools that cannot speak SOCKS use a local bridge)" : ""));
            Ui.Note(console, $"Set by: {proxy.Source}. Used for: {proxy.ScopeDisplay}." + (proxy.Scope.Count == 0 ? " Every model call, subscription login, git and gate command goes through it." : " Everything else connects directly."));
            if (proxy.NoProxy.Count > 0)
            {
                Ui.Note(console, "Skips the proxy: localhost, 127.0.0.1, " + string.Join(", ", proxy.NoProxy));
            }
        }
        else if (proxy.Enabled)
        {
            Ui.Problem(console, $"A proxy is saved but its address is not valid: {proxy.Url}. Fix it with `tokonomy proxy set <host:port>`.");
        }
        else
        {
            console.MarkupLine("[grey]Proxy is OFF: connections go out directly.[/]");
            Ui.Note(console, "Turn it on with: tokonomy proxy set 127.0.0.1:10808   (then: tokonomy proxy test)");
        }

        return ExitCodes.Ok;
    }
}

/// <summary><c>tokonomy proxy set &lt;address&gt;</c>.</summary>
public sealed class ProxySetCommand(IAnsiConsole console) : AsyncCommand<ProxySetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ProxySetSettings settings, CancellationToken cancellationToken)
    {
        if (!ProxySettings.TryNormalize(settings.Address, out var url, out var error))
        {
            Output.Error(console, error);
            return ExitCodes.Usage;
        }

        List<string> scope = [];
        if (settings.Only is not null)
        {
            if (ProxyScopeUi.Parse(settings.Only, out var scopeError) is not { } parsed)
            {
                Output.Error(console, scopeError);
                return ExitCodes.Usage;
            }

            scope = parsed;
        }
        else if (Ui.IsInteractive(console))
        {
            scope = ProxyScopeUi.Ask(console, UserDefaults.Load());
        }

        var proxy = new ProxySettings
        {
            Enabled = true,
            Url = url,
            NoProxy = (settings.NoProxy ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Scope = scope,
        };
        ProxyStore.Use(proxy);
        Ui.Done(console, $"Proxy saved and ON: {proxy.Display}");
        Ui.Note(console, $"Used for: {proxy.ScopeDisplay}. Change it with: tokonomy proxy scope");
        if (!settings.Address.Contains("://", StringComparison.Ordinal))
        {
            Ui.Note(console, "No scheme given, so it is treated as an HTTP proxy. For a SOCKS port use socks5://" + settings.Address.Trim());
        }

        if (Environment.GetEnvironmentVariable(ProxySettings.EnvironmentVariable) is { Length: > 0 })
        {
            Ui.Note(console, $"Note: the {ProxySettings.EnvironmentVariable} environment variable is set and overrides this file.");
        }

        if (proxy.Url.Contains('@'))
        {
            Ui.Note(console, "The proxy password is kept in your user profile file, not in any project. To avoid that, set the TOKONOMY_PROXY environment variable instead.");
        }

        if (settings.NoTest)
        {
            return ExitCodes.Ok;
        }

        var probe = await ProxyDiagnostics.ProbeProxyAsync(ProxySettings.Current, cancellationToken);
        if (probe.Ok)
        {
            Ui.Done(console, $"{probe.Target}: {probe.Detail}");
            Ui.Note(console, "Next: tokonomy proxy test   (checks each model and subscription through it)");
            return ExitCodes.Ok;
        }

        Ui.Problem(console, $"{probe.Target}: {probe.Detail}");
        Ui.Note(console, "It is saved anyway. Start the proxy app, then run: tokonomy proxy test");
        return ExitCodes.Failed;
    }
}

/// <summary><c>tokonomy proxy off</c> and <c>on</c>.</summary>
public sealed class ProxyOffCommand(IAnsiConsole console) : Command<ProxyNoSettings>
{
    public override int Execute(CommandContext context, ProxyNoSettings settings, CancellationToken cancellationToken)
    {
        var saved = ProxySettings.LoadUser(getEnv: _ => null);
        saved.Enabled = false;
        ProxyStore.Use(saved);
        Ui.Done(console, "Proxy is OFF. The address is kept; turn it back on with `tokonomy proxy on`.");
        return ExitCodes.Ok;
    }
}

public sealed class ProxyOnCommand(IAnsiConsole console) : Command<ProxyNoSettings>
{
    public override int Execute(CommandContext context, ProxyNoSettings settings, CancellationToken cancellationToken)
    {
        var saved = ProxySettings.LoadUser(getEnv: _ => null);
        if (!ProxySettings.TryNormalize(saved.Url, out _, out _))
        {
            Output.Error(console, "No proxy address is saved yet. Use: tokonomy proxy set <host:port>");
            return ExitCodes.Usage;
        }

        saved.Enabled = true;
        ProxyStore.Use(saved);
        Ui.Done(console, $"Proxy is ON: {saved.Display}");
        return ExitCodes.Ok;
    }
}

/// <summary><c>tokonomy proxy test</c>: the proxy itself, then every connection you use, through it.</summary>
public sealed class ProxyTestCommand(IAnsiConsole console, IProcessRunner runner) : AsyncCommand<ProxyTestSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ProxyTestSettings settings, CancellationToken cancellationToken)
    {
        var proxy = ProxySettings.Current;
        if (!proxy.IsActive)
        {
            console.MarkupLine("[yellow]The proxy is OFF[/], so these checks go out directly. Turn it on with `tokonomy proxy set <host:port>`.");
        }
        else
        {
            Ui.Step(console, $"Proxy: {proxy.Display} (used for {proxy.ScopeDisplay})");
            var self = await ProxyDiagnostics.ProbeProxyAsync(proxy, cancellationToken);
            Report(self);
            if (!self.Ok)
            {
                return ExitCodes.Failed;
            }
        }

        var defaults = UserDefaults.Load();
        var targets = settings.All ? ProxyDiagnostics.AllTargets : ProxyDiagnostics.UsedTargets(defaults);
        if (!settings.All && defaults is null)
        {
            Ui.Note(console, "No setup found for this user yet, so every known service is checked.");
        }

        var failed = false;
        foreach (var target in targets)
        {
            var result = await Ui.SpinAsync(console, $"Reaching {target.Title}", () => ProxyDiagnostics.ProbeTargetAsync(target, proxy, null, cancellationToken));
            Report(result);
            failed |= !result.Ok;

            if (settings.Live && result.Ok)
            {
                failed |= !await LiveAsync(target, defaults, cancellationToken);
            }
        }

        console.WriteLine();
        if (failed)
        {
            Ui.Problem(console, "Some connections failed through this proxy.");
            Ui.Note(console, "Check the proxy app's routing rules for those hosts, or add them to --no-proxy if they should go direct.");
            return ExitCodes.Failed;
        }

        Ui.Done(console, "Everything you use is reachable" + (proxy.IsActive ? " through the proxy." : "."));
        if (!settings.Live)
        {
            Ui.Note(console, "This checked the route. Add --live to also make a tiny real call to each model.");
        }

        return ExitCodes.Ok;
    }

    private void Report(ProbeResult result)
    {
        var line = $"{result.Target}: {result.Detail} ({result.Elapsed.TotalMilliseconds:0} ms)";
        if (result.Ok)
        {
            Ui.Done(console, line);
        }
        else
        {
            Ui.Problem(console, line);
        }
    }

    private async Task<bool> LiveAsync(ProxyTarget target, UserDefaults? defaults, CancellationToken ct)
    {
        var models = new List<string>();
        if (defaults is not null)
        {
            // Two custom providers share the id "custom": tell them apart by address.
            bool Is(string id, CustomApi? custom) => target.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                && (custom is null || target.Api is null || custom.BaseUrl.TrimEnd('/').Equals(target.Api.BaseUrl, StringComparison.OrdinalIgnoreCase));

            if (Is(defaults.Strong, defaults.StrongCustom) && defaults.ArchitectModel.Length > 0)
            {
                models.Add(defaults.ArchitectModel);
            }

            if (Is(defaults.Cheap, defaults.CheapCustom) && defaults.WorkerModel.Length > 0
                && !models.Contains(defaults.WorkerModel, StringComparer.OrdinalIgnoreCase))
            {
                models.Add(defaults.WorkerModel);
            }
        }

        if (models.Count == 0)
        {
            Ui.Note(console, $"No model is set up for {target.Title}, so no live call was made.");
            return true;
        }

        var ok = true;
        foreach (var model in models)
        {
            try
            {
                IProvider provider = target.Kind switch
                {
                    ConnectionKind.ClaudeCode => new ClaudeCliProvider(runner),
                    ConnectionKind.Antigravity => new AgyCliProvider(runner),
                    ConnectionKind.Codex => new CodexCliProvider(runner),
                    _ => new OpenAiCompatibleProvider(new ProviderConfig { BaseUrl = target.Api!.BaseUrl, ApiKeyEnv = target.Api.KeyVariable }, PriceTable.Empty),
                };
                var started = DateTime.UtcNow;
                await Ui.SpinAsync(console, $"Asking {model} to say OK", () =>
                    provider.CompleteAsync(new ProviderRequest(model, "Reply with the single word OK.", "ping"), ct));
                Ui.Done(console, $"{model}: answered through {(ProxySettings.Current.IsActive ? "the proxy" : "a direct connection")} ({(DateTime.UtcNow - started).TotalSeconds:0.0} s)");
                (provider as IDisposable)?.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ExecutableNotFoundException or MissingApiKeyException or HttpRequestException or System.ClientModel.ClientResultException)
            {
                Ui.Problem(console, $"{model}: {Ui.Shorten(ex.Message, 200)}");
                ok = false;
            }
        }

        return ok;
    }
}

/// <summary><c>tokonomy proxy scope</c>: choose which models or connections use the saved proxy.</summary>
public sealed class ProxyScopeCommand(IAnsiConsole console) : Command<ProxyScopeSettings>
{
    public override int Execute(CommandContext context, ProxyScopeSettings settings, CancellationToken cancellationToken)
    {
        var saved = ProxySettings.LoadUser(getEnv: _ => null);
        if (!ProxySettings.TryNormalize(saved.Url, out _, out _))
        {
            Output.Error(console, "No proxy address is saved yet. Use: tokonomy proxy set <host:port>");
            return ExitCodes.Usage;
        }

        List<string> scope;
        if (settings.Only is not null)
        {
            if (ProxyScopeUi.Parse(settings.Only, out var error) is not { } parsed)
            {
                Output.Error(console, error);
                return ExitCodes.Usage;
            }

            scope = parsed;
        }
        else if (Ui.IsInteractive(console))
        {
            scope = ProxyScopeUi.Ask(console, UserDefaults.Load());
        }
        else
        {
            Ui.Note(console, $"Used for: {saved.ScopeDisplay}. Change it with: tokonomy proxy scope --only claude,codex (or --only all)");
            return ExitCodes.Ok;
        }

        saved.Scope = scope;
        ProxyStore.Use(saved);
        Ui.Done(console, $"The proxy is now used for: {saved.ScopeDisplay}.");
        if (!saved.Enabled)
        {
            Ui.Note(console, "It is currently off: turn it on with `tokonomy proxy on`.");
        }

        return ExitCodes.Ok;
    }
}
