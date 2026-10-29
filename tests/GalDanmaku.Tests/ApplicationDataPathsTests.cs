using GalDanmaku.Core;

namespace GalDanmaku.Tests;

public sealed class ApplicationDataPathsTests
{
    [Fact]
    public void DefaultDataDirectoryFollowsTheExecutableWhenItMoves()
    {
        string first = Path.Combine(Path.GetTempPath(), "first-release");
        string second = Path.Combine(Path.GetTempPath(), "second-release");
        Assert.Equal(Path.Combine(first, "data"), ApplicationDataPaths.ResolveDataDirectory(first));
        Assert.Equal(Path.Combine(second, "data"), ApplicationDataPaths.ResolveDataDirectory(second));
    }

    [Fact]
    public void ExplicitDevelopmentDirectoryResolvesWithoutUsingTheWorkingDirectory()
    {
        string application = Path.Combine(Path.GetTempPath(), "release");
        string isolated = Path.Combine(Path.GetTempPath(), "isolated-data");
        Assert.Equal(isolated, ApplicationDataPaths.ResolveDataDirectory(application, isolated));
        Assert.Equal(Path.Combine(application, "verification-data"), ApplicationDataPaths.ResolveDataDirectory(application, "verification-data"));
    }
}
