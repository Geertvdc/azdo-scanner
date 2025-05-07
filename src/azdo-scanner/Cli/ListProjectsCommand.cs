using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using AzdoScanner.Core;

namespace AzdoScanner.Cli
{
    // Helper record types
    internal record RepoInfo(string Name, string Id, string MainBranchPolicyStatus);
    internal record ServiceConnectionInfo(string Name, string Type, string Id);

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
            // 1. Resolve organization
            string? usedOrg = settings.Organization;
            if (string.IsNullOrWhiteSpace(usedOrg))
            {
                var orgResult = _processRunner.Run("az", "devops configure --list --output json");
                if (orgResult.ExitCode == 0)
                {
                    try
                    {
                        var orgJson = System.Text.Json.JsonDocument.Parse(orgResult.Output);
                        if (orgJson.RootElement.TryGetProperty("organization", out var orgProp))
                        {
                            usedOrg = orgProp.GetString() ?? "";
                        }
                    }
                    catch { }
                }
            }
            if (string.IsNullOrWhiteSpace(usedOrg))
            {
                AnsiConsole.MarkupLine("[red]No organization specified or found in az config.[/]");
                return 1;
            }


            // Normalize org URL if needed
            if (!usedOrg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!usedOrg.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase))
                {
                    usedOrg = $"https://dev.azure.com/{usedOrg.Trim().Trim('/')}";
                }
                else
                {
                    usedOrg = $"https://{usedOrg.Trim().Trim('/')}";
                }
            }

            AnsiConsole.MarkupLine($"[blue]Using organization:[/] {usedOrg}");

            // 2. List projects
            var projectsResult = _processRunner.Run("az", $"devops project list --org {usedOrg} --output json");
            if (projectsResult.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red]Failed to list projects for org {usedOrg}[/]");
                return 1;
            }
            var projectNames = new List<string>();
            try
            {
                var projectsJson = System.Text.Json.JsonDocument.Parse(projectsResult.Output);
                if (projectsJson.RootElement.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var proj in valueProp.EnumerateArray())
                    {
                        if (proj.TryGetProperty("name", out var nameProp))
                        {
                            var name = nameProp.GetString();
                            if (!string.IsNullOrEmpty(name))
                                projectNames.Add(name);
                        }
                    }
                }
            }
            catch { }

            // 3. Filter projects if needed
            if (!string.IsNullOrWhiteSpace(settings.Projects))
            {
                var filter = settings.Projects.Split(',').Select(p => p.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                projectNames = projectNames.Where(p => filter.Contains(p)).ToList();
            }

            // 4. Output as a tree
            AnsiConsole.MarkupLine($"[bold]Organization:[/] {usedOrg}");
            var rootTree = new Tree("[yellow]🛡️ Azure DevOps Projects[/]");

            foreach (var project in projectNames)
            {
                var projectNode = rootTree.AddNode($"[yellow]📁 {project}[/]");

                // Admins
                var admins = GetProjectAdminEmails(project, usedOrg);
                var adminsNode = projectNode.AddNode("[blue]👤 Admins[/]");
                if (admins.Count > 0)
                {
                    foreach (var admin in admins)
                        adminsNode.AddNode($"[blue]{admin}[/]");
                }
                else
                {
                    adminsNode.AddNode("[grey]None[/]");
                }

                // Repos
                if (settings.IncludeRepos)
                {
                    var repos = GetProjectRepos(project, usedOrg);
                    var reposNode = projectNode.AddNode("[green]📦 Repos[/]");
                    if (repos.Count > 0)
                    {
                        foreach (var repo in repos)
                            reposNode.AddNode($"[green]{repo.Name}[/] [dim]({repo.MainBranchPolicyStatus})[/]");
                    }
                    else
                    {
                        reposNode.AddNode("[grey]None[/]");
                    }
                }

                // Service Connections
                if (settings.IncludeServiceConnections)
                {
                    var svcs = GetProjectServiceConnections(project, usedOrg);
                    var svcNode = projectNode.AddNode("[magenta]🔗 Service Connections[/]");
                    if (svcs.Count > 0)
                    {
                        foreach (var svc in svcs)
                            svcNode.AddNode($"[magenta]{svc.Name}[/] [dim]({svc.Type})[/]");
                    }
                    else
                    {
                        svcNode.AddNode("[grey]None[/]");
                    }
                }
            }

            AnsiConsole.Write(rootTree);
            return 0;
        }

        private List<string> GetProjectAdminEmails(string projectName, string usedOrg)
        {
            var emails = new List<string>();
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
                                        emails.Add(email);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            return emails;
        }

        private List<RepoInfo> GetProjectRepos(string projectName, string usedOrg)
        {
            var repos = new List<RepoInfo>();
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
                            repos.Add(new RepoInfo(repoName ?? "", repoId ?? "", policyStatus));
                        }
                    }
                }
                catch { }
            }
            return repos;
        }

        private List<ServiceConnectionInfo> GetProjectServiceConnections(string projectName, string usedOrg)
        {
            var list = new List<ServiceConnectionInfo>();
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
                            list.Add(new ServiceConnectionInfo(svcName ?? "", svcType ?? "", svcId ?? ""));
                        }
                    }
                }
                catch { }
            }
            return list;
        }
    }
}
