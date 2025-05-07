namespace AzdoScanner.Core
{
    public interface IPrerequisiteChecker
    {
        bool CheckAzCli();
        bool CheckAzDevOpsCli();
        bool CheckAll();
    }
}
