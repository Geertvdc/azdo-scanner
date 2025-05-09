using Spectre.Console;
using Spectre.Console.Rendering;
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

    public class ListProjectsCommand : AsyncCommand<ListProjectsCommandSettings>
    {
        private readonly IProcessRunner _processRunner;

        public ListProjectsCommand(IProcessRunner processRunner)
        {
            _processRunner = processRunner;
        }

        public override async Task<int> ExecuteAsync(CommandContext context, ListProjectsCommandSettings settings)
        {
            using var cts = new System.Threading.CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                AnsiConsole.MarkupLine("[yellow]Cancellation requested. Exiting...[/]");
            };
            // ... rest of method ...
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
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning: Failed to parse organization from az config: {ex.Message}[/]");
                    }
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

            // 2. List projects (async)
            List<string> projectNames = new();
            var projectsResult = await Task.Run(() => _processRunner.Run("az", $"devops project list --org {usedOrg} --output json"), cts.Token).ConfigureAwait(false);
            if (projectsResult.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red]Failed to list projects for org {usedOrg}[/]");
                return 1;
            }
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
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to parse project list: {ex.Message}[/]");
                return 1;
            }

            // 3. Filter projects if needed
            if (!string.IsNullOrWhiteSpace(settings.Projects))
            {
                var filter = settings.Projects.Split(',').Select(p => p.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                projectNames = projectNames.Where(p => filter.Contains(p)).ToList();
            }

            AnsiConsole.MarkupLine($"[bold]Organization:[/] {usedOrg}");
            var rootTree = new Tree("[yellow]Azure DevOps Projects[/]");
            var projectNodeMap = new Dictionary<string, TreeNode>();

            var spinner = Spinner.Known.Dots;
            await AnsiConsole.Live(new Panel(rootTree))
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    // Helper to run a spinner with a dynamic message and action
                    async Task RunWithSpinnerAsync(Func<Task> action, Func<string> getMessage)
                    {
                        bool done = false;
                        int frame = 0;
                        var spinnerTask = Task.Run(async () =>
                        {
                            while (!done && !cts.Token.IsCancellationRequested)
                            {
                                var spinnerFrame = spinner.Frames[frame];
                                var spinnerMarkup = $"[grey]{spinnerFrame} {getMessage()}[/]";
                                ctx.UpdateTarget(new Rows(new IRenderable[] { rootTree, new Markup(spinnerMarkup) }));
                                frame = (frame + 1) % spinner.Frames.Count;
                                await Task.Delay(spinner.Interval, cts.Token).ConfigureAwait(false);
                            }
                        }, cts.Token);

                        try
                        {
                            await action().ConfigureAwait(false);
                        }
                        finally
                        {
                            done = true;
                            try { await spinnerTask.ConfigureAwait(false); } catch { /* ignore */ }
                            ctx.UpdateTarget(rootTree);
                            ctx.Refresh();
                        }
                    }

                    for (int i = 0; i < projectNames.Count; i++)
                    {
                        if (cts.Token.IsCancellationRequested)
                            break;

                        var project = projectNames[i];
                        TreeNode? projectNode = rootTree.AddNode(new Markup($"[yellow]📁 {project}[/]"));
                        projectNodeMap[project] = projectNode;
                        ctx.Refresh();

                        await RunWithSpinnerAsync(async () =>
                        {
                            var admins = await Task.Run(() => GetProjectAdminEmails(project, usedOrg), cts.Token).ConfigureAwait(false);
                            var adminsNode = projectNode.AddNode(new Markup("[blue]👤 Admins[/]"));
                            if (admins.Count > 0)
                                foreach (var admin in admins)
                                    adminsNode.AddNode(new Markup($"[blue]{admin}[/]"));
                            else
                                adminsNode.AddNode(new Markup("[grey]None[/]"));
                            ctx.Refresh();
                        }, () => $"Loading admins for project '{project}'...");

                        if (settings.IncludeRepos)
                        {
                            await RunWithSpinnerAsync(async () =>
                            {
                                var repos = await Task.Run(() => GetProjectRepos(project, usedOrg), cts.Token).ConfigureAwait(false);
                                var reposNode = projectNode.AddNode(new Markup("[green]📦 Repos[/]"));
                                if (repos.Count > 0)
                                    foreach (var repo in repos)
                                        reposNode.AddNode(new Markup($"[green]{repo.Name}[/] [dim]({repo.MainBranchPolicyStatus})[/]"));
                                else
                                    reposNode.AddNode(new Markup("[grey]None[/]"));
                                ctx.Refresh();
                            }, () => $"Loading repositories for project '{project}'...");
                        }

                        if (settings.IncludeServiceConnections)
                        {
                            await RunWithSpinnerAsync(async () =>
                            {
                                var svcs = await Task.Run(() => GetProjectServiceConnections(project, usedOrg), cts.Token).ConfigureAwait(false);
                                var svcNode = projectNode.AddNode(new Markup("[magenta]🔗 Service Connections[/]"));
                                if (svcs.Count > 0)
                                    foreach (var svc in svcs)
                                        svcNode.AddNode(new Markup($"[magenta]{svc.Name}[/] [dim]({svc.Type})[/]"));
                                else
                                    svcNode.AddNode(new Markup("[grey]None[/]"));
                                ctx.Refresh();
                            }, () => $"Loading service connections for project '{project}'...");
                        }
                    }
                });
            return 0;
        }

        private List<string> GetProjectAdminEmails(string projectName, string usedOrg)
        {
            var emails = new List<string>();
            try
            {
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
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning: Failed to parse admin group for project {projectName}: {ex.Message}[/]");
                    }
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
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[yellow]Warning: Failed to parse admin members for project {projectName}: {ex.Message}[/]");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Failed to fetch admin emails for project {projectName}: {ex.Message}[/]");
            }
            return emails;
        }

        private List<RepoInfo> GetProjectRepos(string projectName, string usedOrg)
        {
            var repos = new List<RepoInfo>();
            try
            {
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
                                string policyStatus = "[red]✗ No branch policy[/]";
                                List<string> policyIssues = new();
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
                                                bool foundRequiredReviewers = false;
                                                foreach (var policy in policyJson.RootElement.EnumerateArray())
                                                {
                                                    if (policy.TryGetProperty("type", out var typeProp) &&
                                                        ((typeProp.ValueKind == System.Text.Json.JsonValueKind.String && typeProp.GetString() == "Minimum number of reviewers") ||
                                                         (typeProp.ValueKind == System.Text.Json.JsonValueKind.Object && typeProp.TryGetProperty("displayName", out var displayNameProp) && displayNameProp.GetString() == "Minimum number of reviewers")))
                                                    {
                                                        foundRequiredReviewers = true;
                                                        // All relevant settings are under the "settings" property
                                                        if (policy.TryGetProperty("settings", out var settingsProp))
                                                        {
                                                            // minimumApproverCount must be 1 or more
                                                            if (settingsProp.TryGetProperty("minimumApproverCount", out var minApproverCountValue))
                                                            {
                                                                if (minApproverCountValue.ValueKind == System.Text.Json.JsonValueKind.Number && minApproverCountValue.GetInt32() < 1)
                                                                {
                                                                    policyIssues.Add("[red]✗ Minimum number of reviewers is less than 1[/]");
                                                                }
                                                            }
                                                            else
                                                            {
                                                                policyIssues.Add("[red]✗ Minimum number of reviewers setting missing[/]");
                                                            }

                                                            // blockLastPusherVote must be false
                                                            if (settingsProp.TryGetProperty("blockLastPusherVote", out var blockLastPusherVoteValue))
                                                            {
                                                                bool blockLastPusherVoteEnabled = false;
                                                                if (blockLastPusherVoteValue.ValueKind == System.Text.Json.JsonValueKind.True)
                                                                    blockLastPusherVoteEnabled = true;
                                                                else if (blockLastPusherVoteValue.ValueKind == System.Text.Json.JsonValueKind.String &&
                                                                         string.Equals(blockLastPusherVoteValue.GetString(), "true", System.StringComparison.OrdinalIgnoreCase))
                                                                    blockLastPusherVoteEnabled = true;
                                                                if (!blockLastPusherVoteEnabled)
                                                                {
                                                                    policyIssues.Add("[red]✗ Prohibit most recent pusher (blockLastPusherVote) must be true[/]");
                                                                }
                                                            }
                                                            else
                                                            {
                                                                policyIssues.Add("[red]✗ Prohibit most recent pusher (blockLastPusherVote) setting missing[/]");
                                                            }

                                                            // At least one of requireVoteOnLastIteration, requireVoteOnEachIteration, resetRejectionsOnSourcePush must be true
                                                            bool requireVoteOnLastIteration = false;
                                                            bool requireVoteOnEachIteration = false;
                                                            bool resetRejectionsOnSourcePush = false;

                                                            if (settingsProp.TryGetProperty("requireVoteOnLastIteration", out var requireVoteOnLastIterationValue))
                                                            {
                                                                if ((requireVoteOnLastIterationValue.ValueKind == System.Text.Json.JsonValueKind.True) ||
                                                                    (requireVoteOnLastIterationValue.ValueKind == System.Text.Json.JsonValueKind.String &&
                                                                     string.Equals(requireVoteOnLastIterationValue.GetString(), "true", System.StringComparison.OrdinalIgnoreCase)))
                                                                {
                                                                    requireVoteOnLastIteration = true;
                                                                }
                                                            }
                                                            if (settingsProp.TryGetProperty("requireVoteOnEachIteration", out var requireVoteOnEachIterationValue))
                                                            {
                                                                if ((requireVoteOnEachIterationValue.ValueKind == System.Text.Json.JsonValueKind.True) ||
                                                                    (requireVoteOnEachIterationValue.ValueKind == System.Text.Json.JsonValueKind.String &&
                                                                     string.Equals(requireVoteOnEachIterationValue.GetString(), "true", System.StringComparison.OrdinalIgnoreCase)))
                                                                {
                                                                    requireVoteOnEachIteration = true;
                                                                }
                                                            }
                                                            if (settingsProp.TryGetProperty("resetRejectionsOnSourcePush", out var resetRejectionsOnSourcePushValue))
                                                            {
                                                                if ((resetRejectionsOnSourcePushValue.ValueKind == System.Text.Json.JsonValueKind.True) ||
                                                                    (resetRejectionsOnSourcePushValue.ValueKind == System.Text.Json.JsonValueKind.String &&
                                                                     string.Equals(resetRejectionsOnSourcePushValue.GetString(), "true", System.StringComparison.OrdinalIgnoreCase)))
                                                                {
                                                                    resetRejectionsOnSourcePush = true;
                                                                }
                                                            }
                                                            if (!(requireVoteOnLastIteration || requireVoteOnEachIteration || resetRejectionsOnSourcePush))
                                                            {
                                                                policyIssues.Add("[red]✗ At least one of requireVoteOnLastIteration, requireVoteOnEachIteration, or resetRejectionsOnSourcePush must be true[/]");
                                                            }
                                                        }
                                                        else
                                                        {
                                                            policyIssues.Add("[red]✗ Policy settings missing[/]");
                                                        }

                                                        if (policy.TryGetProperty("isEnabled", out var isEnabledProp))
                                                        {
                                                            if (!isEnabledProp.GetBoolean())
                                                                policyIssues.Add("[red]✗ Policy is not enabled[/]");
                                                        }
                                                        else
                                                        {
                                                            policyIssues.Add("[red]✗ Policy enabled setting missing[/]");
                                                        }
                                                    }
                                                }
                                                if (!foundRequiredReviewers)
                                                {
                                                    policyIssues.Add("[red]✗ No 'Minimum number of reviewers' policy found[/]");
                                                }
                                                if (policyIssues.Count == 0)
                                                {
                                                    policyStatus = "[green]✔ All checks passed[/]";
                                                }
                                                else
                                                {
                                                    policyStatus = string.Join("\n", policyIssues);
                                                }
                                            }
                                            else
                                            {
                                                policyStatus = "[red]✗ No branch policy[/]";
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            AnsiConsole.MarkupLine($"[yellow]Warning: Failed to parse branch policy for repo {repoName} in project {projectName}: {ex.Message}[/]");
                                        }
                                    }
                                }
                                repos.Add(new RepoInfo(repoName ?? "", repoId ?? "", policyStatus));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning: Failed to parse repos for project {projectName}: {ex.Message}[/]");
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Failed to fetch repos for project {projectName}: {ex.Message}[/]");
            }
            return repos;
        }

        private List<ServiceConnectionInfo> GetProjectServiceConnections(string projectName, string usedOrg)
        {
            var list = new List<ServiceConnectionInfo>();
            try
            {
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
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning: Failed to parse service connections for project {projectName}: {ex.Message}[/]");
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Failed to fetch service connections for project {projectName}: {ex.Message}[/]");
            }
            return list;
        }
    }
}
