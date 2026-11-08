using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;
using Taskrail.Runtime.Config;

namespace Taskrail.Cli.Infrastructure;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int ConfigError = 3;
    public const int NotImplemented = 64;
}

/// <summary>Options shared by every command.</summary>
public class CommonSettings : CommandSettings
{
    [CommandOption("-c|--config <DIR>")]
    [Description("Path to the .taskrail directory. Defaults to the nearest one at or above the current directory.")]
    public string? Config { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Show detailed output.")]
    public bool Verbose { get; init; }

    [CommandOption("--json")]
    [Description("Write machine-readable JSON to stdout.")]
    public bool Json { get; init; }
}

internal static class Output
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Json<T>(IAnsiConsole console, T value) =>
        console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    public static void Error(IAnsiConsole console, string message) =>
        console.MarkupLine($"[red]error:[/] {Markup.Escape(message)}");

    public static void Warn(IAnsiConsole console, string message) =>
        console.MarkupLine($"[yellow]warning:[/] {Markup.Escape(message)}");

    public static void Issues(IAnsiConsole console, IEnumerable<ConfigIssue> issues)
    {
        foreach (var issue in issues)
        {
            var color = issue.Severity == Severity.Error ? "red" : "yellow";
            console.MarkupLine($"[{color}]{Markup.Escape(issue.ToString())}[/]");
        }
    }
}

internal static class ConfigLocator
{
    /// <summary>Resolves and loads config, printing issues. Returns null (and sets an exit code) on failure.</summary>
    public static TaskrailConfig? LoadOrReport(IAnsiConsole console, CommonSettings settings, out int exitCode)
    {
        var dir = settings.Config ?? ConfigLoader.FindConfigDirectory(Directory.GetCurrentDirectory());
        if (dir is null || !Directory.Exists(dir))
        {
            Output.Error(console, $"No {TaskrailConfig.DirectoryName} directory found. Run `taskrail init` in your repository first.");
            exitCode = ExitCodes.ConfigError;
            return null;
        }

        var config = ConfigLoader.Load(dir);
        if (config.HasErrors)
        {
            Output.Issues(console, config.Issues);
            exitCode = ExitCodes.ConfigError;
            return null;
        }

        if (settings.Verbose)
        {
            Output.Issues(console, config.Issues);
        }

        exitCode = ExitCodes.Ok;
        return config;
    }
}
