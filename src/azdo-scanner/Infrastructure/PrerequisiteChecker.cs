using System.Diagnostics;
using Spectre.Console;
using AzdoScanner.Core;

namespace AzdoScanner.Infrastructure
{
    public class PrerequisiteChecker : IPrerequisiteChecker
    {
        public bool CheckAzCli()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (process == null)
                    return false;
                process.WaitForExit(3000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public bool CheckAzDevOpsCli()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = "devops configure -l",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (process == null)
                    return false;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                if (process.ExitCode == 0)
                {
                    // Only print a warning if no defaults are configured
                    if (string.IsNullOrWhiteSpace(output) || !output.Contains("[defaults]"))
                    {
                        AnsiConsole.MarkupLine("[yellow]No Azure DevOps defaults are configured.[/]");
                    }
                    return true;
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Failed to get Azure DevOps CLI configuration.[/]");
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        public bool CheckAll()
        {
            var az = CheckAzCli();
            var devops = CheckAzDevOpsCli();
            if (!az)
                AnsiConsole.MarkupLine("[red]Azure CLI (az) is not installed or not available in PATH.[/]");
            if (!devops)
                AnsiConsole.MarkupLine("[red]Azure DevOps CLI extension (az devops) is not installed or not available.[/]");
            return az && devops;
        }
    }
}
