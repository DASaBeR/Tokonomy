using System.Text;
using Spectre.Console;
using Tokonomy.Cli;
using Tokonomy.Core.Net;

Console.OutputEncoding = Encoding.UTF8;
ProxySettings.Current = ProxySettings.LoadUser();
return await TokonomyApp.RunAsync(args, AnsiConsole.Console);
