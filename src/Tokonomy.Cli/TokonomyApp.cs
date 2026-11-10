using Spectre.Console;
using Spectre.Console.Cli;
using Tokonomy.Cli.Commands;
using Tokonomy.Cli.Infrastructure;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Cli;

public static class TokonomyApp
{
    public static async Task<int> RunAsync(string[] args, IAnsiConsole console, IProcessRunner? runner = null, Services? services = null)
    {
        var registrar = new TypeRegistrar();
        registrar.RegisterInstance(typeof(IAnsiConsole), console);
        registrar.RegisterInstance(typeof(IProcessRunner), runner ?? new ProcessRunner());
        registrar.RegisterInstance(typeof(Services), services ?? new Services());

        var app = new CommandApp(registrar);
        app.Configure(config =>
        {
            config.SetApplicationName("tokonomy");
            config.ConfigureConsole(console);
            config.SetExceptionHandler((ex, _) =>
            {
                if (ex is OperationCanceledException)
                {
                    Output.Warn(console, "Cancelled.");
                    return 130;
                }

                Output.Error(console, ex.Message);
                return ex is CommandParseException or CommandRuntimeException ? ExitCodes.Usage : ExitCodes.Failed;
            });

            config.AddCommand<BuildCommand>("build").WithDescription("Do everything in one go: set up, plan, confirm, build and verify, merge.")
                .WithExample("build", "\"a responsive landing page for a SaaS product\"");
            config.AddCommand<InitCommand>("init").WithDescription("Guided setup: pick who plans and who codes (no files to edit).");
            config.AddCommand<DoctorCommand>("doctor").WithDescription("Check git, executors, capabilities, API keys and gate tools.");
            config.AddCommand<PlanCommand>("plan").WithDescription("Step by step: only plan (see the plan, then use `run`).")
                .WithExample("plan", "\"Add rate limiting to /login\"");
            config.AddCommand<RunCommand>("run").WithDescription("Step by step: execute the latest plan.");
            config.AddCommand<LoginCommand>("login").WithDescription("Sign Claude Code, Codex or Antigravity in to its provider (opens the browser).")
                .WithExample("login", "codex");
            config.AddBranch("proxy", proxy =>
            {
                proxy.SetDescription("Send every connection through an HTTP or SOCKS5 proxy (per user).");
                proxy.SetDefaultCommand<ProxyShowCommand>();
                proxy.AddCommand<ProxySetCommand>("set").WithDescription("Save and turn on a proxy.")
                    .WithExample("proxy", "set", "127.0.0.1:10808");
                proxy.AddCommand<ProxyScopeCommand>("scope").WithDescription("Choose which models or connections use the proxy.")
                    .WithExample("proxy", "scope", "--only", "openrouter");
                proxy.AddCommand<ProxyOnCommand>("on").WithDescription("Turn the saved proxy on.");
                proxy.AddCommand<ProxyOffCommand>("off").WithDescription("Turn the proxy off (the address is kept).");
                proxy.AddCommand<ProxyTestCommand>("test").WithDescription("Check the proxy and each model or subscription you use through it.");
            });
            config.AddCommand<ReportCommand>("report").WithDescription("Show the cost and quality ledger.");
            config.AddCommand<BenchCommand>("bench").WithDescription("Compare strategies on a taskset.")
                .WithExample("bench", "tasks.yaml", "--strategies", "1,1b,2", "--repeat", "3");
        });

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // First Ctrl+C cancels gracefully (kills worker process trees, records the run); a second one exits.
            if (!cts.IsCancellationRequested)
            {
                e.Cancel = true;
                cts.Cancel();
            }
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await app.RunAsync(args, cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
