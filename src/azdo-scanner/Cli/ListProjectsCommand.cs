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

        [Description("Include a list of repos for each project.")]
        [CommandOption("--include-repos")]
        public bool IncludeRepos { get; set; }
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
                table.AddColumn("Admin Count");
                table.AddColumn("Admin Emails");
                if (settings.IncludeRepos)
                {
                    table.AddColumn("Repos");
                }
                foreach (var project in projects.EnumerateArray())
                {
                    var projectName = project.GetProperty("name").GetString() ?? "";
                    var projectId = project.GetProperty("id").GetString() ?? "";
                    // Get Project Administrators group descriptor
                    var groupResult = _processRunner.Run(
                        "az",
                        $"devops security group list --project \"{projectName}\" --org {org} --output json");
                    string? adminDescriptor = null;
                    if (groupResult.ExitCode == 0)
                    {
                        try
                        {
                            var groupJson = System.Text.Json.JsonDocument.Parse(groupResult.Output);
                            foreach (var group in groupJson.RootElement.GetProperty("graphGroups").EnumerateArray())
                            {
                                if (group.GetProperty("displayName").GetString() == "Project Administrators")
                                {
                                    adminDescriptor = group.GetProperty("descriptor").GetString();
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                    int adminCount = 0;
                    string adminEmails = "";
                    if (!string.IsNullOrEmpty(adminDescriptor))
                    {
                        var membersResult = _processRunner.Run(
                            "az",
                            $"devops security group membership list --id {adminDescriptor} --org {org} --output json");
                        if (membersResult.ExitCode == 0)
                        {
                            try
                            {
                                var membersJson = System.Text.Json.JsonDocument.Parse(membersResult.Output);
                                var emails = new List<string>();
                                if (membersJson.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    foreach (var member in membersJson.RootElement.EnumerateObject())
                                    {
                                        if (member.Value.TryGetProperty("mailAddress", out var emailProp))
                                        {
                                            var email = emailProp.GetString();
                                            if (!string.IsNullOrEmpty(email))
                                            {
                                                emails.Add(email);
                                            }
                                        }
                                    }
                                }
                                adminCount = emails.Count;
                                adminEmails = string.Join(", ", emails);
                            }
                            catch { }
                        }
                    }
                    string repoNames = "";
                    if (settings.IncludeRepos)
                    {
                        var reposResult = _processRunner.Run(
                            "az",
                            $"repos list --project \"{projectName}\" --org {org} --output json");
                        if (reposResult.ExitCode == 0)
                        {
                            try
                            {
                                var reposJson = System.Text.Json.JsonDocument.Parse(reposResult.Output);
                                if (reposJson.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    var names = new List<string>();
                                    foreach (var repo in reposJson.RootElement.EnumerateArray())
                                    {
                                        if (repo.TryGetProperty("name", out var nameProp))
                                        {
                                            var name = nameProp.GetString();
                                            if (!string.IsNullOrEmpty(name))
                                                names.Add(name);
                                        }
                                    }
                                    repoNames = string.Join(", ", names);
                                }
                            }
                            catch { }
                        }
                    }
                    if (settings.IncludeRepos)
                        table.AddRow(projectName, projectId, adminCount.ToString(), adminEmails, repoNames);
                    else
                        table.AddRow(projectName, projectId, adminCount.ToString(), adminEmails);
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
