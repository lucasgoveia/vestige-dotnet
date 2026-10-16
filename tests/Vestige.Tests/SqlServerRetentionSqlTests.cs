using Vestige.Retention;
using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class SqlServerRetentionSqlTests
{
    private static SqlServerTableLayout Layout()
    {
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        options.PromotedFields.Add("security.event", PromotedType.String(64), indexed: false);
        return new SqlServerTableLayout(options);
    }

    [Fact]
    public void DeleteBatch_WithoutExclusions_FiltersOnTimestampOnly()
    {
        Assert.Equal(
            "DELETE TOP (@batch) FROM [dbo].[wide_events] WHERE [timestamp_utc] < DATEADD(SECOND, -@period_seconds, SYSDATETIMEOFFSET())",
            SqlServerRetentionSql.DeleteBatch(Layout(), new RetentionPass(TimeSpan.FromDays(365), [])));
    }

    [Fact]
    public void DeleteBatch_WithExclusions_SparesRowsCarryingLongerRuleFields()
    {
        Assert.Equal(
            "DELETE TOP (@batch) FROM [dbo].[wide_events] WHERE [timestamp_utc] < DATEADD(SECOND, -@period_seconds, SYSDATETIMEOFFSET()) " +
            "AND [audit_action] IS NULL AND JSON_VALUE([payload], @path_0) IS NULL " +
            "AND [security_event] IS NULL AND JSON_VALUE([payload], @path_1) IS NULL",
            SqlServerRetentionSql.DeleteBatch(Layout(), new RetentionPass(TimeSpan.FromDays(30), ["audit_action", "security.event"])));
    }

    [Theory]
    [InlineData("audit_action", "$.\"audit_action\"")]
    [InlineData("security.event", "$.\"security.event\"")]
    [InlineData("we\"ird\\key", "$.\"we\\\"ird\\\\key\"")]
    public void JsonPath_QuotesKeyAsSingleMember(string key, string expected)
    {
        Assert.Equal(expected, SqlServerRetentionSql.JsonPath(key));
    }
}
