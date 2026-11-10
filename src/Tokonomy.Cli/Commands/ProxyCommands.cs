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

    [CommandOption("--no-test")]
    [Description("Save without checking that the proxy answers.")]
    public bool NoTest { get; init; }
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
            Ui.Note(console, $"Set by: {proxy.Source}. Every model call, subscription login, git and gate command goes through it.");
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

        var proxy = new ProxySettings
        {
            Enabled = true,
            Url = url,
            NoProxy = (settings.NoProxy ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        };
        ProxyStore.Use(proxy);
        Ui.Done(console, $"Proxy saved and ON: {proxy.Display}");
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
            Ui.Step(console, $"Proxy: {proxy.Display}");
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
            if (target.Id.Equals(defaults.Strong, StringComparison.OrdinalIgnoreCase) && defaults.ArchitectModel.Length > 0)
            {
                models.Add(defaults.ArchitectModel);
            }

            if (target.Id.Equals(defaults.Cheap, StringComparison.OrdinalIgnoreCase) && defaults.WorkerModel.Length > 0
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
