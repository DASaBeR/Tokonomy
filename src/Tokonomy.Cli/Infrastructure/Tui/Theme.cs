using Spectre.Console;

namespace Tokonomy.Cli.Infrastructure.Tui;

/// <summary>One small palette and the home-screen banner for the interactive shell.</summary>
internal static class Theme
{
    public static readonly Style Accent = new(Color.Aqua, decoration: Decoration.Bold);

    public static void Banner(IAnsiConsole console, string version)
    {
        var cwd = Directory.GetCurrentDirectory();
        var configured = Directory.Exists(Path.Combine(cwd, ".tokonomy"));

        var body = new Rows(
            new Markup($"[bold aqua]Tokonomy[/] [grey]v{Markup.Escape(version)}[/]"),
            new Markup("[grey]Spend strong-model tokens only where they matter.[/]"),
            new Text(""),
            new Markup($"[grey]folder[/]  {Markup.Escape(cwd)}"),
            new Markup($"[grey]setup [/]  {(configured ? "[green]✓ configured[/]" : "[yellow]not set up yet (choose Build or Setup)[/]")}"));

        console.Write(new Panel(body) { Border = BoxBorder.Rounded, BorderStyle = Style.Parse("aqua"), Padding = new Padding(2, 0) });
        console.WriteLine();
    }
}
