using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class IndexBuildStrategyTests
{
    [Theory]
    [InlineData(3, true)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(6, false)]
    [InlineData(9, false)]
    public void ForEngineEdition_OnlineOnlyWhereSupported(int edition, bool expectOnline)
    {
        Assert.Equal(expectOnline ? IndexBuildMode.Online : IndexBuildMode.Skip, IndexBuildStrategy.ForEngineEdition(edition));
    }
}
