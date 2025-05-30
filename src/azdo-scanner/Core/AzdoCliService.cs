using System.Collections.Generic;
using System.Text.Json;
using AzdoScanner.Core;
// Ensure the types are visible in this file
using RepoInfo = AzdoScanner.Core.RepoInfo;
using ServiceConnectionInfo = AzdoScanner.Core.ServiceConnectionInfo;

namespace AzdoScanner.Core
{
    public class AzdoCliService
    {
        private readonly IProcessRunner _processRunner;
        public AzdoCliService(IProcessRunner processRunner)
        {
            _processRunner = processRunner;
        }

        public List<string> ListProjects(string org)
        {
            var result = _processRunner.Run("az", $"devops project list --org {org} --output json");
            var projects = new List<string>();
            if (result.ExitCode != 0) return projects;
            try
            {
                var json = JsonDocument.Parse(result.Output);
                if (json.RootElement.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var proj in valueProp.EnumerateArray())
                    {
                        if (proj.TryGetProperty("name", out var nameProp))
                        {
                            var name = nameProp.GetString();
                            if (!string.IsNullOrEmpty(name))
                                projects.Add(name);
                        }
                    }
                }
            }
            catch { }
            return projects;
        }

        public List<string> GetProjectAdminEmails(string projectName, string org)
        {
            var emails = new List<string>();
            var groupResult = _processRunner.Run(
                "az",
                $"devops security group list --project \"{projectName}\" --org {org} --output json");
            string? adminDescriptor = null;
            if (groupResult.ExitCode == 0)
            {
                try
                {
                    var groupJson = JsonDocument.Parse(groupResult.Output);
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
                    $"devops security group membership list --id {adminDescriptor} --org {org} --output json");
                if (membersResult.ExitCode == 0)
                {
                    try
                    {
                        var membersJson = JsonDocument.Parse(membersResult.Output);
                        if (membersJson.RootElement.ValueKind == JsonValueKind.Object)
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

        public List<RepoInfo> GetProjectRepos(string projectName, string org)
        {
            var repos = new List<RepoInfo>();
            var reposResult = _processRunner.Run(
                "az",
                $"repos list --project \"{projectName}\" --org {org} --output json");
            if (reposResult.ExitCode == 0)
            {
                try
                {
                    var reposJson = JsonDocument.Parse(reposResult.Output);
                    if (reposJson.RootElement.ValueKind == JsonValueKind.Array && reposJson.RootElement.GetArrayLength() > 0)
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
                                    $"repos policy list --project \"{projectName}\" --org {org} --repository-id {repoId} --branch {branch} --output json");
                                if (policyResult.ExitCode == 0)
                                {
                                    try
                                    {
                                        var policyJson = JsonDocument.Parse(policyResult.Output);
                                        if (policyJson.RootElement.ValueKind == JsonValueKind.Array && policyJson.RootElement.GetArrayLength() > 0)
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
                                                    ((typeProp.ValueKind == JsonValueKind.String && typeProp.GetString() == "Minimum number of reviewers") ||
                                                     (typeProp.ValueKind == JsonValueKind.Object && typeProp.TryGetProperty("displayName", out var displayNameProp) && displayNameProp.GetString() == "Minimum number of reviewers")))
                                                {
                                                    foundRequiredReviewers = true;
                                                    // All relevant settings are under the "settings" property
                                                    if (policy.TryGetProperty("settings", out var settingsProp))
                                                    {
                                                        if (settingsProp.TryGetProperty("minimumApproverCount", out var minApproverCountValue))
                                                        {
                                                            if (minApproverCountValue.ValueKind == JsonValueKind.Number && minApproverCountValue.GetInt32() >= 1)
                                                            {
                                                                hasMinReviewers = true;
                                                            }
                                                        }
                                                        if (settingsProp.TryGetProperty("blockLastPusherVote", out var blockLastPusherVoteValue))
                                                        {
                                                            bool blockLastPusherVoteEnabled = false;
                                                            if (blockLastPusherVoteValue.ValueKind == JsonValueKind.True)
                                                                blockLastPusherVoteEnabled = true;
                                                            else if (blockLastPusherVoteValue.ValueKind == JsonValueKind.String &&
                                                                     string.Equals(blockLastPusherVoteValue.GetString(), "true", System.StringComparison.OrdinalIgnoreCase))
                                                                blockLastPusherVoteEnabled = true;
                                                            if (blockLastPusherVoteEnabled)
                                                            {
                                                                prohibitsLastPusher = true;
                                                            }
                                                        }
                                                        if (settingsProp.TryGetProperty("requireVoteOnLastIteration", out var requireVoteOnLastIterationValue))
                                                        {
                                                            if ((requireVoteOnLastIterationValue.ValueKind == JsonValueKind.True) ||
                                                                (requireVoteOnLastIterationValue.ValueKind == JsonValueKind.String &&
                                                                 string.Equals(requireVoteOnLastIterationValue.GetString(), "true", System.StringComparison.OrdinalIgnoreCase)))
                                                            {
                                                                requireVoteOnLastIteration = true;
                                                            }
                                                        }
                                                        if (settingsProp.TryGetProperty("requireVoteOnEachIteration", out var requireVoteOnEachIterationValue))
                                                        {
                                                            if ((requireVoteOnEachIterationValue.ValueKind == JsonValueKind.True) ||
                                                                (requireVoteOnEachIterationValue.ValueKind == JsonValueKind.String &&
                                                                 string.Equals(requireVoteOnEachIterationValue.GetString(), "true", System.StringComparison.OrdinalIgnoreCase)))
                                                            {
                                                                requireVoteOnEachIteration = true;
                                                            }
                                                        }
                                                        if (settingsProp.TryGetProperty("resetRejectionsOnSourcePush", out var resetRejectionsOnSourcePushValue))
                                                        {
                                                            if ((resetRejectionsOnSourcePushValue.ValueKind == JsonValueKind.True) ||
                                                                (resetRejectionsOnSourcePushValue.ValueKind == JsonValueKind.String &&
                                                                 string.Equals(resetRejectionsOnSourcePushValue.GetString(), "true", System.StringComparison.OrdinalIgnoreCase)))
                                                            {
                                                                resetRejectionsOnSourcePush = true;
                                                            }
                                                        }
                                                    }
                                                }
                                                if (policy.TryGetProperty("isEnabled", out var isEnabledProp))
                                                {
                                                    if (isEnabledProp.ValueKind == JsonValueKind.True && isEnabledProp.GetBoolean())
                                                    {
                                                        policyEnabled = true;
                                                    }
                                                }
                                            }
                                            if (foundRequiredReviewers && policyEnabled)
                                            {
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
                                    catch { }
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
                catch { }
            }
            return repos;
        }

        public List<ServiceConnectionInfo> GetProjectServiceConnections(string projectName, string org)
        {
            var list = new List<ServiceConnectionInfo>();
            var svcResult = _processRunner.Run(
                "az",
                $"devops service-endpoint list --project \"{projectName}\" --org {org} --output json");
            if (svcResult.ExitCode == 0)
            {
                try
                {
                    var svcJson = JsonDocument.Parse(svcResult.Output);
                    if (svcJson.RootElement.ValueKind == JsonValueKind.Array && svcJson.RootElement.GetArrayLength() > 0)
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
