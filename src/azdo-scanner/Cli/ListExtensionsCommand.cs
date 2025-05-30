using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using AzdoScanner.Core;

namespace AzdoScanner.Cli
{
    public class ListExtensionsCommandSettings : CommandSettings
    {
        [Description("The Azure DevOps organization URL. If not provided, uses the default.")]
        [CommandOption("--org <ORG>")]
        public string? Organization { get; set; }
    }

    public class ListExtensionsCommand : AsyncCommand<ListExtensionsCommandSettings>
    {
        private readonly IProcessRunner _processRunner;

        public ListExtensionsCommand(IProcessRunner processRunner)
        {
            _processRunner = processRunner;
        }

        public override async Task<int> ExecuteAsync(CommandContext context, ListExtensionsCommandSettings settings)
        {
            AnsiConsole.Write(new FigletText("ZURE").Color(Color.White));
            AnsiConsole.Write(new FigletText("AzDo Assessor").Color(Color.Grey));

            string? usedOrg = OrganizationResolver.Resolve(settings.Organization, _processRunner);
            if (string.IsNullOrWhiteSpace(usedOrg))
            {
                AnsiConsole.MarkupLine("[red]No organization specified or found in az config.[/]");
                return 1;
            }

            int exitCode = 0;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching extensions...", async ctx =>
                {
                    var result = await Task.Run(() => _processRunner.Run("az", $"devops extension list --org {usedOrg} --output json"));
                    if (result.ExitCode != 0)
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to list extensions for org {usedOrg}[/]");
                        exitCode = 1;
                        return;
                    }

                    try
                    {
                        var extensionsJson = System.Text.Json.JsonDocument.Parse(result.Output);
                        if (extensionsJson.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            // Group extensions by publisher
                            var publisherGroups = new Dictionary<string, List<System.Text.Json.JsonElement>>(StringComparer.OrdinalIgnoreCase);
                            foreach (var ext in extensionsJson.RootElement.EnumerateArray())
                            {
                                var publisher = ext.TryGetProperty("publisherName", out var p) ? p.GetString() ?? "Unknown" : "Unknown";
                                if (!publisherGroups.ContainsKey(publisher))
                                    publisherGroups[publisher] = new List<System.Text.Json.JsonElement>();
                                publisherGroups[publisher].Add(ext);
                            }

                            var rootTree = new Tree("[yellow]Azure DevOps Extensions by Publisher[/]");
                            foreach (var publisher in publisherGroups.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                            {
                                var publisherNode = rootTree.AddNode($"[grey]{publisher}[/]");
                                foreach (var ext in publisherGroups[publisher])
                                {
                                    var name = ext.TryGetProperty("extensionName", out var n) ? n.GetString() : "";
                                    var version = ext.TryGetProperty("version", out var v) ? v.GetString() : "";
                                    var extNode = publisherNode.AddNode($"[yellow]{name}[/] [dim]{version}[/]");

                                    // Scopes: handle both array of objects and array of strings
                                    if (ext.TryGetProperty("scopes", out var scopesProp) && scopesProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    {
                                        foreach (var scopeElement in scopesProp.EnumerateArray())
                                        {
                                            string? scopeValue = null;
                                            if (scopeElement.ValueKind == System.Text.Json.JsonValueKind.String)
                                            {
                                                scopeValue = scopeElement.GetString();
                                            }
                                            else if (scopeElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                                            {
                                                // Try to get scopeValue property if present
                                                scopeValue = scopeElement.TryGetProperty("scopeValue", out var sv) ? sv.GetString() : null;
                                            }
                                            if (!string.IsNullOrEmpty(scopeValue))
                                            {
                                                bool isHighPriv = scopeValue == "vso.build_execute" || scopeValue == "vso.serviceendpoint_manage";
                                                var scopeLabel = isHighPriv
                                                    ? $"[red]{scopeValue} (high privilege)[/]"
                                                    : $"[white]{scopeValue}[/]";
                                                extNode.AddNode(new Markup(scopeLabel));
                                            }
                                        }
                                    }
                                }
                            }
                            AnsiConsole.Write(rootTree);
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[yellow]No extensions found.[/]");
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to parse extension list: {ex.Message}[/]");
                        exitCode = 1;
                    }
                });
            return exitCode;
        }
    }
}
