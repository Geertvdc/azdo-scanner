using Spectre.Console;
using Spectre.Console.Rendering;
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

    public class ListProjectsCommand : AsyncCommand<ListProjectsCommandSettings>
    {
        private readonly IProcessRunner _processRunner;

        public ListProjectsCommand(IProcessRunner processRunner)
        {
            _processRunner = processRunner;
        }

        public override async Task<int> ExecuteAsync(CommandContext context, ListProjectsCommandSettings settings)
        {    

            BannerPrinter.Print();

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


            // 2. List projects (async) using AzdoCliService
            var azdoCli = new AzdoScanner.Core.AzdoCliService(_processRunner);
            List<string> projectNames = await Task.Run(() => azdoCli.ListProjects(usedOrg), cts.Token).ConfigureAwait(false);
            if (projectNames.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]Failed to list projects for org {usedOrg} or no projects found.[/]");
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
                            var admins = await Task.Run(() => azdoCli.GetProjectAdminEmails(project, usedOrg), cts.Token).ConfigureAwait(false);
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
                                var repos = await Task.Run(() => azdoCli.GetProjectRepos(project, usedOrg), cts.Token).ConfigureAwait(false);
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
                                var svcs = await Task.Run(() => azdoCli.GetProjectServiceConnections(project, usedOrg), cts.Token).ConfigureAwait(false);
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

    }
}
