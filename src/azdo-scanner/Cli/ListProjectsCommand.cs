using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using AzdoScanner.Core;

namespace AzdoScanner.Cli
{
    public class ListProjectsCommandSettings : CommandSettings
    {
        [Description("Include a list of service connections for each project.")]
        [CommandOption("--include-serviceconnections")]
        public bool IncludeServiceConnections { get; set; }

        [Description("Comma-separated list of project names to include (optional). If not set, all projects are included.")]
        [CommandOption("--projects <PROJECTS>")]
        public string? Projects { get; set; }

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
            // Parse project filter if provided
            HashSet<string>? projectFilter = null;
            if (!string.IsNullOrWhiteSpace(settings.Projects))
            {
                projectFilter = new HashSet<string>(settings.Projects.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
            }
            string? org = settings.Organization;
            string? usedOrg = org;
            if (!string.IsNullOrWhiteSpace(org) && !org.StartsWith("http", System.StringComparison.OrdinalIgnoreCase))
            {
                usedOrg = $"https://dev.azure.com/{org.Trim('/')}";
            }
            // If no org specified, get the default from az devops config
            if (string.IsNullOrWhiteSpace(usedOrg))
            {
                var configResult = _processRunner.Run("az", "devops configure --list --output json");
                if (configResult.ExitCode == 0)
                {
                    try
                    {
                        var configJson = System.Text.Json.JsonDocument.Parse(configResult.Output);
                        if (configJson.RootElement.TryGetProperty("organization", out var orgProp))
                        {
                            usedOrg = orgProp.GetString();
                        }
                    }
                    catch { }
                }
            }
            var orgArg = string.IsNullOrWhiteSpace(usedOrg) ? "" : $"--org {usedOrg}";
            AnsiConsole.MarkupLine($"[grey]Using organization:[/] [bold]{(usedOrg ?? "(none set, using az default)")}[/]");
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

                if (settings.IncludeRepos)
                {
                    var tree = new Tree("[bold]Azure DevOps Projects[/]");
                    foreach (var project in projects.EnumerateArray())
                    {
                        var projectName = project.GetProperty("name").GetString() ?? "";
                        var projectId = project.GetProperty("id").GetString() ?? "";
                        if (projectFilter != null && !projectFilter.Contains(projectName))
                            continue;
                        // Get Project Administrators group descriptor
                        var groupResult = _processRunner.Run(
                            "az",
                            $"devops security group list --project \"{projectName}\" --org {usedOrg} --output json");
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
                        // Admins as nodes
                        var projectLabel = $"[bold]{projectName}[/] ([grey]{projectId}[/])";
                        var projectNode = tree.AddNode(projectLabel);

                        var adminsNode = projectNode.AddNode("👤 [bold blue]Admins[/]");
                        int adminCount = 0;
                        if (!string.IsNullOrEmpty(adminDescriptor))
                        {
                            var membersResult = _processRunner.Run(
                                "az",
                                $"devops security group membership list --id {adminDescriptor} --org {usedOrg} --output json");
                            if (membersResult.ExitCode == 0)
                            {
                                try
                                {
                                    var membersJson = System.Text.Json.JsonDocument.Parse(membersResult.Output);
                                    if (membersJson.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                                    {
                                        foreach (var member in membersJson.RootElement.EnumerateObject())
                                        {
                                            if (member.Value.TryGetProperty("mailAddress", out var emailProp))
                                            {
                                                var email = emailProp.GetString();
                                                if (!string.IsNullOrEmpty(email))
                                                {
                                                    adminsNode.AddNode($"[blue]{email}[/]");
                                                    adminCount++;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { adminsNode.AddNode("[red]Error parsing admins.[/]"); }
                            }
                            else
                            {
                                adminsNode.AddNode("[red]Error fetching admins.[/]");
                            }
                        }
                        if (adminCount == 0)
                        {
                            adminsNode.AddNode("[grey]No admins found.[/]");
                        }

                        // Add a 'Repos' node under the project
                        var reposNode = projectNode.AddNode("📦 [bold yellow]Repos[/]");

                        var reposResult = _processRunner.Run(
                            "az",
                            $"repos list --project \"{projectName}\" --org {usedOrg} --output json");
                        if (reposResult.ExitCode == 0)
                        {
                            try
                            {
                                var reposJson = System.Text.Json.JsonDocument.Parse(reposResult.Output);
                                if (reposJson.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && reposJson.RootElement.GetArrayLength() > 0)
                                {
                                    foreach (var repo in reposJson.RootElement.EnumerateArray())
                                    {
                                        string? repoName = null;
                                        string? repoId = null;
                                        if (repo.TryGetProperty("name", out var nameProp))
                                            repoName = nameProp.GetString();
                                        if (repo.TryGetProperty("id", out var idProp))
                                            repoId = idProp.GetString();
                                        // Check branch policy for main branch
                                        string branch = "main";
                                        string policyStatus = "[red]✗[/]";
                                        if (!string.IsNullOrEmpty(repoId))
                                        {
                                            var policyResult = _processRunner.Run(
                                                "az",
                                                $"repos policy list --project \"{projectName}\" --org {usedOrg} --repository-id {repoId} --branch {branch} --output json");
                                            if (policyResult.ExitCode == 0)
                                            {
                                                try
                                                {
                                                    var policyJson = System.Text.Json.JsonDocument.Parse(policyResult.Output);
                                                    if (policyJson.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && policyJson.RootElement.GetArrayLength() > 0)
                                                    {
                                                        policyStatus = "[green]✔[/]";
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                        var repoLabel = $"[yellow]{repoName}[/] [grey]({branch})[/] Policy: {policyStatus}";
                                        reposNode.AddNode(repoLabel);
                                    }
                                }
                                else
                                {
                                    reposNode.AddNode("[grey]No repositories found.[/]");
                                }
                            }
                            catch { reposNode.AddNode("[red]Error parsing repos.[/]"); }
                        }
                        else
                        {
                            reposNode.AddNode("[red]Error fetching repos.[/]");
                        }

                        // Add a 'Service Connections' node under the project if requested
                        if (settings.IncludeServiceConnections)
                        {
                            var svcNode = projectNode.AddNode("🔗 [bold green]Service Connections[/]");
                            var svcResult = _processRunner.Run(
                                "az",
                                $"devops service-endpoint list --project \"{projectName}\" --org {usedOrg} --output json");
                            if (svcResult.ExitCode == 0)
                            {
                                try
                                {
                                    var svcJson = System.Text.Json.JsonDocument.Parse(svcResult.Output);
                                    if (svcJson.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && svcJson.RootElement.GetArrayLength() > 0)
                                    {
                                        foreach (var svc in svcJson.RootElement.EnumerateArray())
                                        {
                                            string? svcName = svc.TryGetProperty("name", out var n) ? n.GetString() : null;
                                            string? svcType = svc.TryGetProperty("type", out var t) ? t.GetString() : null;
                                            string? svcId = svc.TryGetProperty("id", out var i) ? i.GetString() : null;
                                            var svcLabel = $"[green]{svcName}[/] [grey]({svcType})[/] [dim]{svcId}[/]";
                                            svcNode.AddNode(svcLabel);
                                        }
                                    }
                                    else
                                    {
                                        svcNode.AddNode("[grey]No service connections found.[/]");
                                    }
                                }
                                catch { svcNode.AddNode("[red]Error parsing service connections.[/]"); }
                            }
                            else
                            {
                                svcNode.AddNode("[red]Error fetching service connections.[/]");
                            }
                        }
                    }
                    AnsiConsole.Write(tree);
                }
                else
                {
                    // Table output for project-only info
                    var table = new Table();
                    table.AddColumn("Project Name");
                    table.AddColumn("ID");
                    table.AddColumn("Admin Count");
                    table.AddColumn("Admin Emails");
                    foreach (var project in projects.EnumerateArray())
                    {
                        var projectName = project.GetProperty("name").GetString() ?? "";
                        var projectId = project.GetProperty("id").GetString() ?? "";
                        if (projectFilter != null && !projectFilter.Contains(projectName))
                            continue;
                        // Get Project Administrators group descriptor
                        var groupResult = _processRunner.Run(
                            "az",
                            $"devops security group list --project \"{projectName}\" --org {usedOrg} --output json");
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
                                $"devops security group membership list --id {adminDescriptor} --org {usedOrg} --output json");
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
                        table.AddRow(projectName, projectId, adminCount.ToString(), adminEmails);
                    }
                    AnsiConsole.Write(table);
                }
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
