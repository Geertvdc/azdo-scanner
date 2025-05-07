using Spectre.Console;
using AzdoScanner.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Spectre.Console.Cli;
using AzdoScanner.Core;
using AzdoScanner.Infrastructure;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((hostingContext, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
    })
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<IGreeter, Greeter>();
        services.AddTransient<HelloCommand>();
        services.AddTransient<ListProjectsCommand>();
        services.AddSingleton<IPrerequisiteChecker, PrerequisiteChecker>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
    })
    .Build();

// Check prerequisites before running the CLI
var prereqChecker = host.Services.GetRequiredService<IPrerequisiteChecker>();
if (!prereqChecker.CheckAll())
{
    AnsiConsole.MarkupLine("[red]Exiting due to missing prerequisites.[/]");
    return 1;
}
var app = new CommandApp(new AzdoScanner.TypeRegistrar(host.Services));

app.Configure(config =>
{
    config.AddCommand<HelloCommand>("hello").WithDescription("Prints a hello message");
    config.AddCommand<ListProjectsCommand>("list-projects").WithDescription("Lists all Azure DevOps projects in the organization");
});

return app.Run(args);
