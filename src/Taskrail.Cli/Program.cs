using System.Text;
using Spectre.Console;
using Taskrail.Cli;

Console.OutputEncoding = Encoding.UTF8;
return await TaskrailApp.RunAsync(args, AnsiConsole.Console);
