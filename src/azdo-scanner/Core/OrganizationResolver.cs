using System;
using AzdoScanner.Core;

namespace AzdoScanner.Core
{
    public static class OrganizationResolver
    {
        /// <summary>
        /// Resolves the Azure DevOps organization URL from the provided value or from az config using the given process runner.
        /// </summary>
        /// <param name="organization">The organization URL or name (may be null).</param>
        /// <param name="processRunner">The process runner to use for az CLI calls.</param>
        /// <returns>The resolved and normalized organization URL, or null if not found.</returns>
        public static string? Resolve(string? organization, IProcessRunner processRunner)
        {
            string? usedOrg = organization;
            if (string.IsNullOrWhiteSpace(usedOrg))
            {
                var orgResult = processRunner.Run("az", "devops configure --list --output json");
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
                    catch
                    {
                        // Ignore parse errors, will handle as not found
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(usedOrg))
            {
                return null;
            }
            // Normalize org URL
            if (!usedOrg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!usedOrg.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase))
                    usedOrg = $"https://dev.azure.com/{usedOrg.Trim().Trim('/')}";
                else
                    usedOrg = $"https://{usedOrg.Trim().Trim('/')}";
            }
            return usedOrg;
        }
    }
}
