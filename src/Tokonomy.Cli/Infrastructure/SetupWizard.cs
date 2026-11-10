using Spectre.Console;
using Tokonomy.Core.Abstractions;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Process;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Cli.Infrastructure;

/// <summary>What the user already decided on the command line. Anything left null is asked (interactive) or defaulted (--yes).</summary>
public sealed record SetupOptions
{
    /// <summary>Shortcut that sets both roles: <c>claude-antigravity</c>, <c>claude</c> or <c>api</c>.</summary>
    public string? Preset { get; init; }

    public string? Strong { get; init; }
    public string? Cheap { get; init; }
    public string? Architect { get; init; }
    public string? Worker { get; init; }
    public bool? Acceptance { get; init; }

    /// <summary><c>windows-sandbox</c> or <c>host-unsafe</c>.</summary>
    public string? Isolation { get; init; }

    /// <summary>Never ask: take flags, then sensible defaults.</summary>
    public bool Yes { get; init; }

    public bool Force { get; init; }
}

/// <summary>
/// First-run setup in a few questions instead of hand-editing YAML. It looks at the machine first, then asks for two
/// independent choices (the strong model and the cheap model), each with its own connection (a subscription login or an API
/// key), its own readiness check, and its own model menu.
/// </summary>
internal sealed class SetupWizard(IAnsiConsole console, IProcessRunner runner, Func<HttpClient>? httpFactory = null)
{
    private sealed record Pick(string Id, string Label);

    private HttpClient? _http;
    private HttpClient Http => _http ??= httpFactory?.Invoke() ?? new HttpClient(Tokonomy.Core.Net.ProxySettings.Current.CreateHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };

    private IReadOnlyList<CatalogModel> _openRouterMenu = [];
    private IReadOnlyList<CatalogModel> _openRouterAll = [];
    private bool _openRouterTried;
    private readonly Dictionary<string, string> _keysThisSession = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ScaffoldResult?> RunAsync(string root, SetupOptions options, CancellationToken ct)
    {
        var interactive = !options.Yes && Ui.IsInteractive(console);
        var stacks = StackDetector.Detect(root);
        console.MarkupLine(stacks.Count == 0
            ? "[grey]No project files recognised yet (new or empty project).[/]"
            : $"[grey]Detected:[/] {Markup.Escape(string.Join(", ", stacks.Select(s => s.Name)))}");

        var report = await Ui.SpinAsync(console, "Looking at what is installed on this machine", () => MachineScan.ScanAsync(runner, root, ct));
        if (!report.Git.Installed)
        {
            Output.Error(console, "Git is not installed, and Tokonomy needs it to keep your project safe while it works.");
            Ui.Note(console, "Install it with: " + report.Git.InstallHint);
            return null;
        }

        ShowMachine(report);

        if (!ChooseConnections(report, options, interactive, out var strong, out var cheap))
        {
            return null;
        }

        var architect = options.Architect ?? await PickModelAsync(strong, strongRole: true, report, interactive, ct);
        var worker = options.Worker ?? await PickModelAsync(cheap, strongRole: false, report, interactive, ct);

        var detected = Sources.DetectAcceptance(root);
        var wantAcceptance = options.Acceptance ?? (detected is not null && (interactive ? AskAcceptance(stacks.Count == 0) : stacks.Count == 0));
        if (wantAcceptance && detected is null)
        {
            Output.Warn(console, "Tests written by the strong model need Python or Node.js on this machine; neither was found, so they are off.");
            Ui.Note(console, $"Install Python ({report.Python.InstallHint}) or Node.js ({report.Node.InstallHint}) and run `tokonomy init --force` to turn them on.");
            wantAcceptance = false;
        }

        var isolation = ChooseIsolation(report, options, interactive);
        if (isolation is null)
        {
            return null;
        }

        var choices = new SetupChoices
        {
            Strong = strong,
            Cheap = cheap,
            ArchitectModel = architect,
            WorkerModel = worker,
            Acceptance = wantAcceptance ? detected : null,
            Prices = PricesOf(strong, architect, cheap, worker),
            GateExecution = isolation,
        };

        await CheckConnectionsAsync(choices, interactive, ct);

        var result = Scaffolder.Init(root, options.Force, choices);
        UserDefaults.Save(choices);
        Summary(choices, result);
        Prerequisites(choices, report);
        return result;
    }

    // --- what this machine has -----------------------------------------------------------------------------------

    private void ShowMachine(MachineReport report)
    {
        console.WriteLine();
        console.MarkupLine("[bold]What I found on this machine[/]");
        Line(report.Claude, "Claude Code");
        Line(report.Antigravity, "Antigravity (agy)");
        Line(report.OpenCode, "OpenCode");
        var tests = report.Python.Installed ? "Python" : report.Node.Installed ? "Node.js" : null;
        console.MarkupLine(tests is null
            ? "  [yellow]![/] Python / Node.js  [grey]not found: the strong model cannot write tests until one is installed[/]"
            : $"  [green]✓[/] {tests}  [grey]the strong model can write tests[/]");
        if (report.WindowsSandbox is { } sandbox)
        {
            console.MarkupLine(sandbox.Installed
                ? "  [green]✓[/] Windows Sandbox  [grey]tests run in a safe, throwaway VM[/]"
                : $"  [yellow]![/] Windows Sandbox  [grey]{Markup.Escape(sandbox.Detail)}[/]");
        }

        if (!report.HasCodingAgent)
        {
            console.WriteLine();
            console.MarkupLine("[yellow]No coding agent is installed.[/] The cheap model writes files through one of: Claude Code, Antigravity or OpenCode.");
            Ui.Note(console, "Easiest to add: OpenCode (works with any API key, including free OpenRouter models): " + report.OpenCode.InstallHint);
            Ui.Note(console, "You can still go through the setup and install one afterwards.");
        }
    }

    private void Line(ToolState tool, string title)
    {
        if (tool.Ready)
        {
            console.MarkupLine($"  [green]✓[/] {Markup.Escape(title)}  [grey]{Markup.Escape(tool.Detail)}[/]");
        }
        else if (tool.Installed)
        {
            console.MarkupLine($"  [yellow]![/] {Markup.Escape(title)}  [grey]{Markup.Escape(tool.Detail)}; {Markup.Escape(tool.InstallHint)}[/]");
        }
        else
        {
            console.MarkupLine($"  [red]✗[/] {Markup.Escape(title)}  [grey]not installed; to add it: {Markup.Escape(tool.InstallHint)}[/]");
        }
    }

    // --- the two connection choices -------------------------------------------------------------------------------

    private bool ChooseConnections(MachineReport report, SetupOptions options, bool interactive, out Connection strong, out Connection cheap)
    {
        (strong, cheap) = (Connection.Claude, Connection.Claude);
        var recommended = Recommend(report);
        var fromFlags = false;

        if (options.Preset is not null)
        {
            if (!Sources.TryPreset(options.Preset, out strong, out cheap))
            {
                Output.Error(console, $"Unknown preset '{options.Preset}'. Use claude-antigravity, claude or api, or pick the roles with --strong and --cheap.");
                return false;
            }

            fromFlags = true;
        }

        var strongGiven = options.Preset is not null;
        var cheapGiven = options.Preset is not null;
        if (options.Strong is not null)
        {
            if (!Sources.TryParse(options.Strong, out strong))
            {
                Output.Error(console, $"Unknown --strong '{options.Strong}'. Use claude, antigravity, openrouter, anthropic, gemini, openai or deepseek.");
                return false;
            }

            strongGiven = fromFlags = true;
        }

        if (options.Cheap is not null)
        {
            if (!Sources.TryParse(options.Cheap, out cheap))
            {
                Output.Error(console, $"Unknown --cheap '{options.Cheap}'. Use claude, antigravity, openrouter, anthropic, gemini, openai or deepseek.");
                return false;
            }

            cheapGiven = fromFlags = true;
        }

        if (!strongGiven && !cheapGiven && interactive && UserDefaults.Load() is { IsComplete: true } last && TryRestore(last, out var lastStrong, out var lastCheap)
            && AskReuse(last, lastStrong, lastCheap))
        {
            (strong, cheap) = (lastStrong, lastCheap);
            return true;
        }

        if (!strongGiven)
        {
            if (interactive)
            {
                strong = ChooseConnection(report, strongRole: true, recommended.Strong);
            }
            else
            {
                strong = recommended.Strong;
            }
        }

        if (!cheapGiven)
        {
            if (interactive)
            {
                cheap = ChooseConnection(report, strongRole: false, recommended.Cheap ?? Connection.For(Sources.OpenRouter));
            }
            else if (recommended.Cheap is { } fallback)
            {
                cheap = fallback;
            }
            else
            {
                Output.Error(console, "No coding agent is installed, so there is nothing to write the code with.");
                Ui.Note(console, "Install one (Claude Code, Antigravity or OpenCode), or run `tokonomy init` without --yes for guidance.");
                return false;
            }
        }

        if (!interactive && fromFlags)
        {
            ReportNotReady(report, strong, strongRole: true);
            ReportNotReady(report, cheap, strongRole: false);
        }

        return true;
    }

    /// <summary>Claude plans if it is ready, else Antigravity, else an API. The cheap role needs an agent CLI: Antigravity, Claude, or OpenCode with an API.</summary>
    private static (Connection Strong, Connection? Cheap) Recommend(MachineReport report)
    {
        var strong = report.Claude.Ready ? Connection.Claude
            : report.Antigravity.Ready ? Connection.Antigravity
            : Connection.For(Sources.OpenRouter);
        Connection? cheap = report.Antigravity.Ready ? Connection.Antigravity
            : report.Claude.Ready ? Connection.Claude
            : report.OpenCode.Installed ? Connection.For(Sources.OpenRouter)
            : null;
        return (strong, cheap);
    }

    private static bool TryRestore(UserDefaults last, out Connection strong, out Connection cheap)
    {
        strong = cheap = Connection.Claude;
        return Sources.TryParse(last.Strong, out strong) && Sources.TryParse(last.Cheap, out cheap);
    }

    private bool AskReuse(UserDefaults last, Connection strong, Connection cheap)
    {
        console.WriteLine();
        console.MarkupLine("[bold]Your last setup[/]");
        console.MarkupLine($"  strong: {Markup.Escape(last.ArchitectModel.Length > 0 ? last.ArchitectModel : "(no model)")} [grey]via {Markup.Escape(strong.Title)}[/]");
        console.MarkupLine($"  cheap:  {Markup.Escape(last.WorkerModel.Length > 0 ? last.WorkerModel : "(no model)")} [grey]via {Markup.Escape(cheap.Title)}[/]");
        return console.Confirm("Use the same setup for this project?", true);
    }

    private void ReportNotReady(MachineReport report, Connection connection, bool strongRole)
    {
        foreach (var (problem, hint) in Problems(report, connection, strongRole))
        {
            Output.Warn(console, $"{(strongRole ? "Strong" : "Cheap")} model: {problem}. {hint}");
        }
    }

    private enum Vendor
    {
        Claude,
        Gemini,
        OpenRouter,
        Other,
    }

    /// <summary>Pick a vendor, then how to reach it (subscription or key), then make sure that works; offer another choice when it does not.</summary>
    private Connection ChooseConnection(MachineReport report, bool strongRole, Connection recommended)
    {
        while (true)
        {
            var connection = AskVendor(report, strongRole, recommended);
            var problems = Problems(report, connection, strongRole);
            if (problems.Count == 0)
            {
                return connection;
            }

            console.WriteLine();
            foreach (var (problem, hint) in problems)
            {
                console.MarkupLine($"[yellow]![/] {Markup.Escape(connection.Title)}: {Markup.Escape(problem)}");
                Ui.Note(console, hint);
            }

            var again = console.Prompt(new SelectionPrompt<string>()
                .Title("What now?")
                .AddChoices("Pick a different connection", "Keep this one and finish installing it later")
                .UseConverter(Markup.Escape));
            if (again.StartsWith("Keep", StringComparison.Ordinal))
            {
                return connection;
            }
        }
    }

    private static List<(string Problem, string Hint)> Problems(MachineReport report, Connection connection, bool strongRole)
    {
        var problems = new List<(string, string)>();
        switch (connection.Kind)
        {
            case ConnectionKind.ClaudeCode when !report.Claude.Installed:
                problems.Add(("Claude Code is not installed", report.Claude.InstallHint));
                break;
            case ConnectionKind.ClaudeCode when report.Claude.SignedIn == false:
                problems.Add(("Claude Code is not signed in", report.Claude.InstallHint));
                break;
            case ConnectionKind.Antigravity when !report.Antigravity.Installed:
                problems.Add(("the Antigravity CLI is not installed", report.Antigravity.InstallHint));
                break;
            case ConnectionKind.Antigravity when report.Antigravity.SignedIn == false:
                problems.Add(("Antigravity is not signed in", report.Antigravity.InstallHint));
                break;
        }

        if (connection.IsApi && !strongRole && !report.OpenCode.Installed)
        {
            problems.Add(("OpenCode is not installed, and the cheap model needs a coding agent to write files", report.OpenCode.InstallHint));
        }

        return problems;
    }

    private Connection AskVendor(MachineReport report, bool strongRole, Connection recommended)
    {
        string KeyState(ApiService s) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(s.KeyVariable))
            ? $"[grey]{s.KeyVariable} not set yet[/]"
            : $"[green]✓ {s.KeyVariable} set[/]";

        string CliState(ToolState tool) => tool.Ready ? $"[green]✓ {Markup.Escape(tool.Detail)}[/]"
            : tool.Installed ? $"[yellow]! {Markup.Escape(tool.Detail)}[/]" : "[red]✗ not installed[/]";

        var recommendedVendor = recommended.Kind switch
        {
            ConnectionKind.ClaudeCode => Vendor.Claude,
            ConnectionKind.Antigravity => Vendor.Gemini,
            _ => recommended.Api!.Id switch { "anthropic" => Vendor.Claude, "gemini" => Vendor.Gemini, "openrouter" => Vendor.OpenRouter, _ => Vendor.Other },
        };

        string Label(Vendor vendor)
        {
            var text = vendor switch
            {
                Vendor.Claude => $"Claude (Anthropic)  [grey]· Claude Code subscription: {CliState(report.Claude)}  · or an Anthropic API key[/]",
                Vendor.Gemini => $"Gemini (Google)  [grey]· Antigravity subscription: {CliState(report.Antigravity)}  · or a Gemini API key[/]",
                Vendor.OpenRouter => $"OpenRouter  [grey]· any model, API key: {KeyState(Sources.OpenRouter)}[/]",
                _ => "Another API  [grey]· OpenAI, DeepSeek, or any OpenAI-compatible endpoint[/]",
            };
            return text + (vendor == recommendedVendor ? "  [blue]recommended[/]" : "");
        }

        console.WriteLine();
        var vendor = console.Prompt(new SelectionPrompt<Vendor>()
            .Title(strongRole
                ? "Which [bold]strong model[/] do you want? [grey](it plans the work, rescues hard steps, writes the tests)[/]"
                : "Which [bold]cheap model[/] do you want? [grey](it writes the code, so it also needs a coding agent installed)[/]")
            .AddChoices(Enum.GetValues<Vendor>().OrderByDescending(v => v == recommendedVendor))
            .UseConverter(Label));

        switch (vendor)
        {
            case Vendor.OpenRouter:
                return Connection.For(Sources.OpenRouter);
            case Vendor.Claude:
                return console.Prompt(new SelectionPrompt<Connection>()
                    .Title("How do you use Claude?")
                    .AddChoices(Connection.Claude, Connection.For(Sources.Anthropic))
                    .UseConverter(c => c.Kind == ConnectionKind.ClaudeCode
                        ? $"I have a Claude subscription (Pro/Max)  [grey]· through Claude Code: {CliState(report.Claude)}[/]"
                        : $"I have an Anthropic API key  [grey]· {KeyState(Sources.Anthropic)}{Sources.Anthropic.Tested}[/]"));
            case Vendor.Gemini:
                return console.Prompt(new SelectionPrompt<Connection>()
                    .Title("How do you use Gemini?")
                    .AddChoices(Connection.Antigravity, Connection.For(Sources.Gemini))
                    .UseConverter(c => c.Kind == ConnectionKind.Antigravity
                        ? $"I have an Antigravity subscription  [grey]· through the agy CLI: {CliState(report.Antigravity)} (Antigravity has no API-key mode)[/]"
                        : $"I have a Gemini API key  [grey]· Google AI Studio, {KeyState(Sources.Gemini)}{Sources.Gemini.Tested}[/]"));
            default:
                var service = console.Prompt(new SelectionPrompt<string>()
                    .Title("Which API?")
                    .AddChoices("openai", "deepseek", "custom")
                    .UseConverter(id => id switch
                    {
                        "openai" => $"OpenAI  [grey]· {KeyState(Sources.OpenAi)}{Sources.OpenAi.Tested}[/]",
                        "deepseek" => $"DeepSeek  [grey]· {KeyState(Sources.DeepSeek)}{Sources.DeepSeek.Tested}[/]",
                        _ => "Another OpenAI-compatible endpoint  [grey]· you give the address and key variable[/]",
                    }));
                return service switch
                {
                    "openai" => Connection.For(Sources.OpenAi),
                    "deepseek" => Connection.For(Sources.DeepSeek),
                    _ => Connection.For(AskCustomApi()),
                };
        }
    }

    private ApiService AskCustomApi()
    {
        var url = console.Prompt(new TextPrompt<string>("Base URL of the API [grey](ends in /v1, e.g. http://localhost:11434/v1)[/]:")
            .Validate(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https")
                ? ValidationResult.Success() : ValidationResult.Error("[red]Enter a full http(s) address[/]")));
        var variable = console.Prompt(new TextPrompt<string>("Name of the environment variable that holds its key:")
            .DefaultValue("CUSTOM_API_KEY")
            .Validate(v => System.Text.RegularExpressions.Regex.IsMatch(v, "^[A-Za-z_][A-Za-z0-9_]{0,63}$")
                ? ValidationResult.Success() : ValidationResult.Error("[red]That should be a variable name, not the key itself[/]")));
        return new ApiService("custom", new Uri(url).Host, url.TrimEnd('/'), variable, "", Verified: false);
    }

    // --- keys -----------------------------------------------------------------------------------------------------

    /// <summary>The key for an API connection: found in the environment, or asked for, checked and remembered. Never written into the project.</summary>
    private async Task<string?> EnsureKeyAsync(ApiService service, bool interactive, CancellationToken ct)
    {
        if (_keysThisSession.TryGetValue(service.KeyVariable, out var known))
        {
            return known;
        }

        var key = Environment.GetEnvironmentVariable(service.KeyVariable);
        if (!string.IsNullOrWhiteSpace(key))
        {
            var check = await Ui.SpinAsync(console, $"Checking {service.KeyVariable}", () => ApiModels.CheckKeyAsync(Http, service.BaseUrl, key, service.Id == "openrouter", ct));
            if (check.Ok)
            {
                Ui.Done(console, $"{service.KeyVariable} found in your environment ({check.Message}).");
                _keysThisSession[service.KeyVariable] = key;
                return key;
            }

            Ui.Problem(console, $"{service.KeyVariable} is set, but {check.Message}.");
        }

        if (!interactive)
        {
            return null;
        }

        console.WriteLine();
        console.MarkupLine($"[bold]{Markup.Escape(service.Title)} needs an API key.[/]" +
                           (service.KeyUrl.Length > 0 ? $" Get one at [blue]{Markup.Escape(service.KeyUrl)}[/]." : ""));
        console.MarkupLine($"[grey]It is kept only as the {service.KeyVariable} environment variable of your user account, never in a file of this project.[/]");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var typed = console.Prompt(new TextPrompt<string>($"Paste your {Markup.Escape(service.Title)} key [grey](Enter to skip and add it later)[/]:").Secret().AllowEmpty());
            if (string.IsNullOrWhiteSpace(typed))
            {
                return null;
            }

            typed = typed.Trim();
            var check = await Ui.SpinAsync(console, "Checking the key", () => ApiModels.CheckKeyAsync(Http, service.BaseUrl, typed, service.Id == "openrouter", ct));
            if (!check.Ok)
            {
                Ui.Problem(console, $"{Markup.Escape(check.Message)}. Check that you copied the whole key.");
                continue;
            }

            Ui.Done(console, check.Message + ".");
            Environment.SetEnvironmentVariable(service.KeyVariable, typed);
            _keysThisSession[service.KeyVariable] = typed;
            Remember(service, typed);
            return typed;
        }

        return null;
    }

    private void Remember(ApiService service, string key)
    {
        if (OperatingSystem.IsWindows())
        {
            if (console.Confirm($"Remember it for future terminals? [grey](saves the user environment variable {service.KeyVariable})[/]", true))
            {
                Environment.SetEnvironmentVariable(service.KeyVariable, key, EnvironmentVariableTarget.User);
                Ui.Done(console, $"Saved as {service.KeyVariable}. New terminals will have it; this one already does.");
            }
            else
            {
                Ui.Note(console, "Not saved: you will need to set it again in a new terminal.");
            }

            return;
        }

        Ui.Note(console, $"To keep it for future terminals add this line to your shell profile: export {service.KeyVariable}=\"<your key>\"");
    }

    // --- one model menu per connection ----------------------------------------------------------------------------

    private async Task<string> PickModelAsync(Connection connection, bool strongRole, MachineReport report, bool interactive, CancellationToken ct)
    {
        var role = strongRole ? "strong" : "cheap";
        switch (connection.Kind)
        {
            case ConnectionKind.ClaudeCode:
            {
                var fallback = strongRole ? Sources.DefaultStrong(connection) : Sources.DefaultCheap(connection);
                if (!interactive)
                {
                    return fallback;
                }

                console.WriteLine();
                console.MarkupLine("[grey]Claude Code cannot list the models of your plan, so these are the ones it accepts; your choice is tested below.[/]");
                var models = strongRole ? Sources.ClaudeModels : Sources.ClaudeModels.Reverse().ToList();
                return Menu($"Which Claude model for the [bold]{role}[/] role?",
                    models.Select(m => new Pick(m.Id, $"{m.Id}  [grey]{m.Note}[/]")).ToList(), fallback);
            }

            case ConnectionKind.Antigravity:
            {
                var offered = report.AntigravityModels;
                if (offered.Count == 0)
                {
                    Output.Warn(console, "Could not list models from `agy models` (is Antigravity signed in?).");
                    return interactive
                        ? console.Prompt(new TextPrompt<string>($"Type an Antigravity model id for the [bold]{role}[/] role:").AllowEmpty())
                        : Sources.DefaultCheap(connection);
                }

                var pick = strongRole
                    ? offered.FirstOrDefault(m => m.Contains("pro", StringComparison.OrdinalIgnoreCase)) ?? offered[0]
                    : offered.Contains(Sources.DefaultCheap(connection)) ? Sources.DefaultCheap(connection) : offered[0];
                if (!interactive)
                {
                    return pick;
                }

                console.WriteLine();
                console.MarkupLine($"[grey]{offered.Count} model(s) in your Antigravity plan (from `agy models`).[/]");
                return Menu($"Which Antigravity model for the [bold]{role}[/] role?", offered.Select(m => new Pick(m, Markup.Escape(m))).ToList(), pick);
            }

            default:
                return await PickApiModelAsync(connection.Api!, strongRole, interactive, ct);
        }
    }

    private async Task<string> PickApiModelAsync(ApiService service, bool strongRole, bool interactive, CancellationToken ct)
    {
        var role = strongRole ? "strong" : "cheap";
        var key = await EnsureKeyAsync(service, interactive, ct);

        if (service.Id == Sources.OpenRouter.Id)
        {
            return await PickOpenRouterModelAsync(strongRole, interactive, ct);
        }

        var listed = await Ui.SpinAsync(console, $"Loading {service.Title} models", () => ApiModels.ForServiceAsync(Http, service, key, ct));
        if (listed.Count == 0)
        {
            Output.Warn(console, $"Could not load the {service.Title} model list{(key is null ? " (no key yet)" : "")}.");
            return interactive
                ? console.Prompt(new TextPrompt<string>($"Type a model id for the [bold]{role}[/] role:").AllowEmpty())
                : "";
        }

        if (!interactive)
        {
            return listed[0];
        }

        console.WriteLine();
        console.MarkupLine($"[grey]{listed.Count} model(s) from {Markup.Escape(service.Title)}{(listed.Count > 25 ? " (newest 25 shown)" : "")}.[/]");
        var picks = listed.Take(25).Select(m => new Pick(m, Markup.Escape(m))).ToList();
        picks.Add(new Pick("", "[grey]Other: type a model id…[/]"));
        var chosen = Menu($"Which {Markup.Escape(service.Title)} model for the [bold]{role}[/] role?", picks, listed[0]);
        return chosen.Length > 0 ? chosen : console.Prompt(new TextPrompt<string>("Model id:").AllowEmpty());
    }

    private async Task<string> PickOpenRouterModelAsync(bool strongRole, bool interactive, CancellationToken ct)
    {
        await LoadOpenRouterAsync(ct);
        var role = strongRole ? "strong" : "cheap";
        if (_openRouterMenu.Count == 0)
        {
            Output.Warn(console, "Could not load the OpenRouter model list (offline?).");
            return interactive
                ? console.Prompt(new TextPrompt<string>($"Type a model id for the [bold]{role}[/] role [grey](e.g. anthropic/claude-sonnet-5-5)[/]:").AllowEmpty())
                : "";
        }

        var ordered = strongRole ? OpenRouterCatalog.ForStrongRole(_openRouterMenu) : OpenRouterCatalog.ForCheapRole(_openRouterMenu);
        var fallback = (strongRole
            ? ordered.FirstOrDefault(m => m.Vendor == "anthropic") ?? ordered[0]
            : ordered.FirstOrDefault(m => !m.IsFree) ?? ordered[0]).Id;
        if (!interactive)
        {
            return fallback;
        }

        console.WriteLine();
        console.MarkupLine($"[grey]{_openRouterMenu.Count} important models from OpenRouter ({_openRouterMenu.Count(m => m.IsFree)} free). Prices are USD per million tokens (in / out).[/]");
        var picks = ordered.Select(m => new Pick(m.Id, Label(m))).ToList();
        picks.Add(new Pick("", "[grey]Other: type a model id…[/]"));
        var chosen = Menu(strongRole ? "Which OpenRouter model for the [bold]strong[/] role?" : "Which OpenRouter model for the [bold]cheap[/] role? [grey](free models first)[/]", picks, fallback);
        return chosen.Length > 0 ? chosen : console.Prompt(new TextPrompt<string>("Model id:").AllowEmpty());
    }

    private static string Label(CatalogModel m)
    {
        var price = m.IsFree ? "[green]free[/]" : $"${m.InputPerM:0.##} / ${m.OutputPerM:0.##}";
        return $"{Markup.Escape(m.Id)}  [grey]{price} · {m.ContextLength / 1000}k context[/]";
    }

    private string Menu(string title, List<Pick> picks, string defaultId)
    {
        var ordered = picks.OrderByDescending(p => p.Id == defaultId && p.Id.Length > 0).ToList();
        return console.Prompt(new SelectionPrompt<Pick>()
            .Title(title)
            .PageSize(14)
            .MoreChoicesText("[grey](move up and down for more)[/]")
            .AddChoices(ordered)
            .UseConverter(p => p.Label + (p.Id == defaultId && p.Id.Length > 0 ? "  [blue]default[/]" : ""))).Id;
    }

    private async Task LoadOpenRouterAsync(CancellationToken ct)
    {
        if (_openRouterTried)
        {
            return;
        }

        _openRouterTried = true;
        try
        {
            var all = await Ui.SpinAsync(console, "Loading the OpenRouter model list", () => OpenRouterCatalog.FetchAsync(Http, ct));
            _openRouterAll = all;
            _openRouterMenu = OpenRouterCatalog.Curate(all);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _openRouterMenu = [];
        }
    }

    private Dictionary<string, (decimal Input, decimal Output)> PricesOf(Connection strong, string architect, Connection cheap, string worker)
    {
        var prices = new Dictionary<string, (decimal Input, decimal Output)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (connection, model) in new[] { (strong, architect), (cheap, worker) })
        {
            if (connection.Api?.Id == Sources.OpenRouter.Id &&
                _openRouterAll.FirstOrDefault(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                prices[found.Id] = (found.InputPerM, found.OutputPerM);
            }
        }

        return prices;
    }

    // --- where the worker's code is tested -----------------------------------------------------------------------

    private string? ChooseIsolation(MachineReport report, SetupOptions options, bool interactive)
    {
        if (options.Isolation is { } given)
        {
            if (given is not ("windows-sandbox" or "host-unsafe"))
            {
                Output.Error(console, "--isolation must be windows-sandbox or host-unsafe.");
                return null;
            }

            return given;
        }

        if (report.WindowsSandbox?.Installed == true)
        {
            return "windows-sandbox";
        }

        var why = report.WindowsSandbox is null
            ? "Tokonomy's safe test environment (Windows Sandbox) only exists on Windows."
            : "Windows Sandbox is not available on this computer.";
        if (!interactive)
        {
            Output.Warn(console, why + " The setup keeps the safe option, so `build` will stop until it is available; pass --isolation host-unsafe to accept the risk instead.");
            return "windows-sandbox";
        }

        console.WriteLine();
        console.MarkupLine($"[yellow]![/] {Markup.Escape(why)}");
        console.MarkupLine("While building, the cheap model writes code and then Tokonomy runs that code's tests. A safe room keeps a bad test from touching your files.");
        var choices = new List<string> { "Run the tests directly on this computer (less safe)" };
        if (report.WindowsSandbox is not null)
        {
            choices.Insert(0, "Set up Windows Sandbox later (the build will wait for it)");
            Ui.Note(console, report.WindowsSandbox.InstallHint);
        }

        var pick = console.Prompt(new SelectionPrompt<string>().Title("What should I do?").AddChoices(choices).UseConverter(Markup.Escape));
        return pick.StartsWith("Run", StringComparison.Ordinal) ? "host-unsafe" : "windows-sandbox";
    }

    // --- tests of the connections, and what is still missing ------------------------------------------------------

    private async Task CheckConnectionsAsync(SetupChoices choices, bool interactive, CancellationToken ct)
    {
        var claudeModels = new[] { (choices.Strong, choices.ArchitectModel), (choices.Cheap, choices.WorkerModel) }
            .Where(p => p.Item1.Kind == ConnectionKind.ClaudeCode && p.Item2.Length > 0)
            .Select(p => p.Item2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var agyPlanner = choices.Strong.Kind == ConnectionKind.Antigravity && choices.ArchitectModel.Length > 0;
        if (!interactive || (claudeModels.Count == 0 && !agyPlanner))
        {
            return;
        }

        console.WriteLine();
        var targets = claudeModels.Select(m => $"{m} (Claude Code)").Concat(agyPlanner ? [$"{choices.ArchitectModel} (Antigravity)"] : []);
        if (!console.Confirm($"Test the connection for {string.Join(" and ", targets)} now? [grey](a few tokens each)[/]", true))
        {
            return;
        }

        foreach (var model in claudeModels)
        {
            await ProbeAsync(model, new ClaudeCliProvider(runner), "your Claude Code login", ct);
        }

        if (agyPlanner)
        {
            await ProbeAsync(choices.ArchitectModel, new AgyCliProvider(runner), "your Antigravity login", ct);
        }
    }

    private async Task ProbeAsync(string model, IProvider provider, string login, CancellationToken ct)
    {
        try
        {
            await Ui.SpinAsync(console, $"Asking {model} to say OK", () => provider.CompleteAsync(new ProviderRequest(model, "Reply with the single word OK.", "ping"), ct));
            Ui.Done(console, $"{model} works on {login}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ExecutableNotFoundException)
        {
            Ui.Problem(console, $"{model} did not answer: {Ui.Shorten(ex.Message, 160)}");
            Ui.Note(console, "It may not be part of your plan, or the login has expired. You can change it later with `tokonomy init --force`.");
        }
    }

    private void Summary(SetupChoices choices, ScaffoldResult result)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("", "Model", "Connection");
        table.AddRow("[bold]strong[/] [grey](plans, rescues, tests)[/]", Markup.Escape(Name(choices.ArchitectModel)), Markup.Escape(choices.Strong.Title));
        table.AddRow("[bold]cheap[/] [grey](writes the code)[/]", Markup.Escape(Name(choices.WorkerModel)), Markup.Escape(choices.Cheap.Title));
        console.WriteLine();
        console.Write(table);
        Ui.Done(console, "Setup saved in .tokonomy/ (nothing else to edit)." +
                         (choices.Acceptance is not null ? " The strong model writes the tests." : "") +
                         (choices.GateExecution == "host-unsafe" ? " Tests run directly on this computer." : ""));
        if (result.Skipped.Count > 0)
        {
            Ui.Note(console, "Kept your existing " + string.Join(", ", result.Skipped) + " (use --force to rewrite).");
        }

        Ui.Note(console, "Change anything later with: tokonomy init --force");
    }

    private static string Name(string model) => model.Length > 0 ? model : "(none chosen)";

    private void Prerequisites(SetupChoices choices, MachineReport report)
    {
        foreach (var service in choices.ApiServices)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(service.KeyVariable)))
            {
                console.WriteLine();
                Output.Warn(console, $"{service.Title} needs an API key in the {service.KeyVariable} environment variable (it is never stored in a file).");
                Ui.Note(console, OperatingSystem.IsWindows()
                    ? $"Windows:  setx {service.KeyVariable} \"<your key>\"   then open a new terminal." + (service.KeyUrl.Length > 0 ? $"   Get a key: {service.KeyUrl}" : "")
                    : $"export {service.KeyVariable}=\"<your key>\"   (add it to your shell profile)." + (service.KeyUrl.Length > 0 ? $"   Get a key: {service.KeyUrl}" : ""));
            }
        }

        foreach (var (connection, strongRole) in new[] { (choices.Strong, true), (choices.Cheap, false) })
        {
            foreach (var (problem, hint) in Problems(report, connection, strongRole))
            {
                Output.Warn(console, $"{(strongRole ? "Strong" : "Cheap")} model: {problem}.");
                Ui.Note(console, hint);
            }
        }

        if (choices.ArchitectModel.Length == 0 || choices.WorkerModel.Length == 0)
        {
            Output.Warn(console, "A model name is empty: pass --architect and --worker, or run `tokonomy init --force` and pick from the menus.");
        }

        foreach (var service in new[] { choices.Strong.Api, choices.Cheap.Api }.OfType<ApiService>().Where(s => !s.Verified).DistinctBy(s => s.Id))
        {
            Ui.Note(console, $"{service.Title} uses its OpenAI-compatible endpoint; it follows the same protocol as the tested services but has not been run end to end here.");
        }
    }

    private bool AskAcceptance(bool recommended)
    {
        console.WriteLine();
        console.MarkupLine("[bold]Acceptance tests.[/] The strong model writes the tests that decide whether each step is done; the cheap model cannot change them.");
        console.MarkupLine(recommended
            ? "[grey]Recommended: this project has no test setup to judge the result.[/]"
            : "[grey]This project already has build/test commands; the extra tests would be additional checks.[/]");
        return console.Confirm("Let the strong model write the tests?", recommended);
    }
}
