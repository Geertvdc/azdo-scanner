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
            AnsiConsole.Write(new FigletText("ZURE").Color(Color.White));
            AnsiConsole.Write(new FigletText("AzDo Assessor").Color(Color.Grey));

            using var cts = new System.Threading.CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                AnsiConsole.MarkupLine("[yellow]Cancellation requested. Exiting...[/]");
            };
            // 1. Resolve organization
            string? usedOrg = OrganizationResolver.Resolve(settings.Organization, _processRunner);
            if (string.IsNullOrWhiteSpace(usedOrg))
            {
                AnsiConsole.MarkupLine("[red]No organization specified or found in az config.[/]");
                return 1;
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
                                var reposNode = projectNode.AddNode(new Markup("[deepskyblue1]📦 Repos[/]"));
                                if (repos.Count > 0)
                                {
                                    foreach (var repo in repos)
                                    {
                                        var repoNode = reposNode.AddNode(new Markup($"[deepskyblue1]{repo.Name}[/]"));
                                        // Split the policy status into individual lines (checks)
                                        var checks = repo.MainBranchPolicyStatus.Split('\n');
                                        if (checks.Length == 1 && checks[0].Contains("✔ All checks passed"))
                                        {
                                            repoNode.AddNode(new Markup("[green]✔ All checks passed[/]"));
                                        }
                                        else
                                        {
                                            foreach (var check in checks)
                                            {
                                                if (!string.IsNullOrWhiteSpace(check))
                                                    repoNode.AddNode(new Markup(check));
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    reposNode.AddNode(new Markup("[grey]None[/]"));
                                }
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
                                List<string> policyChecks = new();
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
                                                bool hasMinReviewers = false;
                                                bool prohibitsLastPusher = false;
                                                bool policyEnabled = false;
                                                bool requireVoteOnLastIteration = false;
                                                bool requireVoteOnEachIteration = false;
                                                bool resetRejectionsOnSourcePush = false;
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
                                                            if (settingsProp.TryGetProperty("minimumApproverCount", out var minApproverCountValue))
                                                            {
                                                                if (minApproverCountValue.ValueKind == System.Text.Json.JsonValueKind.Number && minApproverCountValue.GetInt32() >= 1)
                                                                {
                                                                    hasMinReviewers = true;
                                                                }
                                                            }

                                                            // blockLastPusherVote must be true
                                                            if (settingsProp.TryGetProperty("blockLastPusherVote", out var blockLastPusherVoteValue))
                                                            {
                                                                bool blockLastPusherVoteEnabled = false;
                                                                if (blockLastPusherVoteValue.ValueKind == System.Text.Json.JsonValueKind.True)
                                                                    blockLastPusherVoteEnabled = true;
                                                                else if (blockLastPusherVoteValue.ValueKind == System.Text.Json.JsonValueKind.String &&
                                                                         string.Equals(blockLastPusherVoteValue.GetString(), "true", System.StringComparison.OrdinalIgnoreCase))
                                                                    blockLastPusherVoteEnabled = true;
                                                                if (blockLastPusherVoteEnabled)
                                                                {
                                                                    prohibitsLastPusher = true;
                                                                }
                                                            }

                                                            // At least one of requireVoteOnLastIteration, requireVoteOnEachIteration, resetRejectionsOnSourcePush must be true
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
                                                        }
                                                    }
                                                    // Check if policy is enabled
                                                    if (policy.TryGetProperty("isEnabled", out var isEnabledProp))
                                                    {
                                                        if (isEnabledProp.ValueKind == System.Text.Json.JsonValueKind.True && isEnabledProp.GetBoolean())
                                                        {
                                                            policyEnabled = true;
                                                        }
                                                    }
                                                }
                                                // Now, add green/red lines for each check
                                                if (foundRequiredReviewers && policyEnabled)
                                                {
                                                    // All checks
                                                    if (hasMinReviewers)
                                                        policyChecks.Add("[green]✔ Has 1 or more reviewer required[/]");
                                                    else
                                                        policyChecks.Add("[red]✗ Minimum number of reviewers is less than 1[/]");

                                                    if (prohibitsLastPusher)
                                                        policyChecks.Add("[green]✔ Prohibits last pusher to approve changes[/]");
                                                    else
                                                        policyChecks.Add("[red]✗ Prohibit most recent pusher (blockLastPusherVote) must be true[/]");

                                                    if (requireVoteOnLastIteration || requireVoteOnEachIteration || resetRejectionsOnSourcePush)
                                                        policyChecks.Add("[green]✔ Votes reset on changes[/]");
                                                    else
                                                        policyChecks.Add("[red]✗ At least one of requireVoteOnLastIteration, requireVoteOnEachIteration, or resetRejectionsOnSourcePush must be true[/]");
                                                }
                                                else
                                                {
                                                    policyChecks.Add("[red]✗ No branch policy (policy not enabled or missing required reviewers policy)[/]");
                                                }
                                            }
                                            else
                                            {
                                                policyChecks.Add("[red]✗ No branch policy[/]");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            AnsiConsole.MarkupLine($"[yellow]Warning: Failed to parse branch policy for repo {repoName} in project {projectName}: {ex.Message}[/]");
                                        }
                                    }
                                }
                                else
                                {
                                    policyChecks.Add("[red]✗ No branch policy[/]");
                                }
                                repos.Add(new RepoInfo(repoName ?? "", repoId ?? "", string.Join("\n", policyChecks)));
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
