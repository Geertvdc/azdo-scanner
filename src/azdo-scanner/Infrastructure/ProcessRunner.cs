using System.Diagnostics;
using AzdoScanner.Core;

namespace AzdoScanner.Infrastructure
{
    public class ProcessRunner : IProcessRunner
    {
        public ProcessResult Run(string fileName, string arguments, int timeoutMs = 10000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null)
            {
                return new ProcessResult { Output = string.Empty, Error = "Failed to start process.", ExitCode = -1 };
            }
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(timeoutMs);
            return new ProcessResult
            {
                Output = output,
                Error = error,
                ExitCode = process.ExitCode
            };
        }
    }
}
