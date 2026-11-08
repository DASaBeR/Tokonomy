using Spectre.Console;
using Spectre.Console.Cli;
using Taskrail.Cli.Commands;
using Taskrail.Cli.Infrastructure;
using Taskrail.Runtime.Process;

namespace Taskrail.Cli;

public static class TaskrailApp
{
    public static async Task<int> RunAsync(string[] args, IAnsiConsole console, IProcessRunner? runner = null)
    {
        var registrar = new TypeRegistrar();
        registrar.RegisterInstance(typeof(IAnsiConsole), console);
        registrar.RegisterInstance(typeof(IProcessRunner), runner ?? new ProcessRunner());

        var app = new CommandApp(registrar);
        app.Configure(config =>
        {
            config.SetApplicationName("taskrail");
            config.ConfigureConsole(console);
            config.SetExceptionHandler((ex, _) =>
            {
                Output.Error(console, ex.Message);
                return ex is CommandParseException or CommandRuntimeException ? ExitCodes.Usage : ExitCodes.Failed;
            });

            config.AddCommand<InitCommand>("init").WithDescription("Detect the stack and scaffold .taskrail/ config.");
            config.AddCommand<DoctorCommand>("doctor").WithDescription("Check git, executors, capabilities, API keys and gate tools.");
            config.AddCommand<PlanCommand>("plan").WithDescription("Architect produces a contract and unit DAG.")
                .WithExample("plan", "\"Add rate limiting to /login\"");
            config.AddCommand<RunCommand>("run").WithDescription("Execute units under supervision.");
            config.AddCommand<ReportCommand>("report").WithDescription("Show the cost and quality ledger.");
            config.AddCommand<BenchCommand>("bench").WithDescription("Compare strategies on a taskset.");
        });

        return await app.RunAsync(args);
    }
}
