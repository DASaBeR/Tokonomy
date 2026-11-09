using System.Text;
using Spectre.Console;
using Tokonomy.Cli;

Console.OutputEncoding = Encoding.UTF8;
return await TokonomyApp.RunAsync(args, AnsiConsole.Console);
