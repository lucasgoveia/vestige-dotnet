using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class IndexBuildStrategyTests
{
    [Theory]
    [InlineData(5, 12, "OnlineLowPriority")]
    [InlineData(8, 12, "OnlineLowPriority")]
    [InlineData(3, 16, "OnlineLowPriority")]
    [InlineData(3, 15, "Online")]
    [InlineData(3, 13, "Online")]
    [InlineData(2, 16, "Skip")]
    [InlineData(4, 16, "Skip")]
    [InlineData(1, 15, "Skip")]
    [InlineData(6, 16, "Skip")]
    public void ForServer_PicksTheLeastBlockingSupportedBuild(int edition, int majorVersion, string expected)
    {
        Assert.Equal(Enum.Parse<IndexBuildMode>(expected), IndexBuildStrategy.ForServer(edition, majorVersion));
    }
}
