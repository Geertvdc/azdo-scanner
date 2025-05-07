namespace AzdoScanner.Core
{
    public class ProcessResult
    {
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public int ExitCode { get; set; }
    }

    public interface IProcessRunner
    {
        ProcessResult Run(string fileName, string arguments, int timeoutMs = 10000);
    }
}
