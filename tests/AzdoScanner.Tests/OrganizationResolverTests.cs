using AzdoScanner.Core;

namespace AzdoScanner.Tests;

public class FakeProcessRunner : IProcessRunner
{
    public ProcessResult Result { get; set; } = new();
    public string? LastFileName { get; private set; }
    public string? LastArguments { get; private set; }

    public ProcessResult Run(string fileName, string arguments, int timeoutMs = 10000)
    {
        LastFileName = fileName;
        LastArguments = arguments;
        return Result;
    }
}

public class OrganizationResolverTests
{
    [Fact]
    public void ReturnsExplicitOrganizationUnchanged()
    {
        var runner = new FakeProcessRunner();
        var org = "https://dev.azure.com/myorg";
        var result = OrganizationResolver.Resolve(org, runner);
        Assert.Equal(org, result);
        Assert.Null(runner.LastFileName); // no call when org provided
    }

    [Fact]
    public void RetrievesOrganizationFromAzConfig()
    {
        var runner = new FakeProcessRunner();
        runner.Result = new ProcessResult
        {
            ExitCode = 0,
            Output = "{\"organization\":\"dev.azure.com/mockorg\"}"
        };
        var result = OrganizationResolver.Resolve(null, runner);
        Assert.Equal("https://dev.azure.com/mockorg", result);
        Assert.Equal("az", runner.LastFileName);
        Assert.Contains("devops configure --list", runner.LastArguments);
    }

    [Theory]
    [InlineData("myorg", "https://dev.azure.com/myorg")]
    [InlineData("dev.azure.com/myorg", "https://dev.azure.com/myorg")]
    [InlineData("https://dev.azure.com/myorg/", "https://dev.azure.com/myorg/")]
    public void NormalizesOrganizationUrls(string input, string expected)
    {
        var runner = new FakeProcessRunner();
        var result = OrganizationResolver.Resolve(input, runner);
        Assert.Equal(expected, result);
    }
}
