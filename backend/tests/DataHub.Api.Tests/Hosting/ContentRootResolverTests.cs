using DataHub.Api.Hosting;
using DataHub.Api.Tests.Support;

namespace DataHub.Api.Tests.Hosting;

public sealed class ContentRootResolverTests : IDisposable
{
    private readonly TempDirectory _workingDirectory = new();
    private readonly TempDirectory _appDirectory = new();

    public void Dispose()
    {
        _workingDirectory.Dispose();
        _appDirectory.Dispose();
    }

    [Fact]
    public void WorkingDirectoryWithSettings_Wins_SoDotnetRunAndDockerKeepTheirBehaviour()
    {
        File.WriteAllText(_workingDirectory.File("appsettings.json"), "{}");
        File.WriteAllText(_appDirectory.File("appsettings.json"), "{}");

        var root = ContentRootResolver.Resolve(_workingDirectory.FullPath, _appDirectory.FullPath);

        Assert.Equal(_workingDirectory.FullPath, root);
    }

    [Fact]
    public void PublishedAppStartedFromAnotherDirectory_UsesTheAppDirectory()
    {
        File.WriteAllText(_appDirectory.File("appsettings.json"), "{}");

        var root = ContentRootResolver.Resolve(_workingDirectory.FullPath, _appDirectory.FullPath);

        Assert.Equal(_appDirectory.FullPath, root);
    }

    [Fact]
    public void TrailingSeparatorOnTheAppDirectory_IsTrimmed()
    {
        File.WriteAllText(_appDirectory.File("appsettings.json"), "{}");
        var withSeparator = _appDirectory.FullPath + Path.DirectorySeparatorChar;

        var root = ContentRootResolver.Resolve(_workingDirectory.FullPath, withSeparator);

        Assert.Equal(_appDirectory.FullPath, root);
    }

    [Fact]
    public void NoSettingsAnywhere_FallsBackToTheWorkingDirectory()
    {
        var root = ContentRootResolver.Resolve(_workingDirectory.FullPath, _appDirectory.FullPath);

        Assert.Equal(_workingDirectory.FullPath, root);
    }
}
