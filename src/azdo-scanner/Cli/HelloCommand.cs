using Spectre.Console;
using Spectre.Console.Cli;
using AzdoScanner.Core;

namespace AzdoScanner.Cli
{
    public class HelloCommand : Command<HelloCommandSettings>
    {
        private readonly IGreeter _greeter;
        public HelloCommand(IGreeter greeter)
        {
            _greeter = greeter;
        }
        public override int Execute(CommandContext context, HelloCommandSettings settings)
        {
            AnsiConsole.MarkupLine($"[bold green]{_greeter.GetGreeting()}[/]");
            return 0;
        }
    }
}
