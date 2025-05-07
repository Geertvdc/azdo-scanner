using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using AzdoScanner.Core;

namespace AzdoScanner.Cli
{
    public class ListProjectsCommandSettings : CommandSettings
    {
        [Description("The Azure DevOps organization URL. If not provided, uses the default.")]
        [CommandOption("--org <ORG>")]
        public string? Organization { get; set; }
    }

    public class ListProjectsCommand : Command<ListProjectsCommandSettings>
    {
        private readonly IProcessRunner _processRunner;

        public ListProjectsCommand(IProcessRunner processRunner)
        {
            _processRunner = processRunner;
        }

        public override int Execute(CommandContext context, ListProjectsCommandSettings settings)
        {
            string? org = settings.Organization;
            if (!string.IsNullOrWhiteSpace(org) && !org.StartsWith("http", System.StringComparison.OrdinalIgnoreCase))
            {
                org = $"https://dev.azure.com/{org.Trim('/')}";
            }
            var orgArg = string.IsNullOrWhiteSpace(org) ? "" : $"--org {org}";
            var result = _processRunner.Run("az", $"devops project list {orgArg} --output json");
            if (result.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red]az devops CLI error:[/] {result.Error}");
                return 1;
            }
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(result.Output);
                var projects = json.RootElement.GetProperty("value");
                if (projects.GetArrayLength() == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No projects found.[/]");
                    return 0;
                }
                var table = new Table();
                table.AddColumn("Project Name");
                table.AddColumn("ID");
                foreach (var project in projects.EnumerateArray())
                {
                    table.AddRow(project.GetProperty("name").GetString() ?? "", project.GetProperty("id").GetString() ?? "");
                }
                AnsiConsole.Write(table);
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Exception:[/] {ex.Message}");
                return 1;
            }
        }
    }
}
