using Vestige.Retention;
using Vestige.Sinks.Postgres;

namespace Vestige.Tests;

public sealed class PostgresRetentionSqlTests
{
    [Fact]
    public void DeleteBatch_WithoutExclusions()
    {
        Assert.Equal(
            "DELETE FROM \"vestige\".\"wide_events\" WHERE ctid IN (SELECT ctid FROM \"vestige\".\"wide_events\" WHERE timestamp_utc < now() - make_interval(secs => @period_seconds) LIMIT @batch)",
            PostgresRetentionSql.DeleteBatch("vestige", "wide_events", new RetentionPass(TimeSpan.FromDays(365), [])));
    }

    [Fact]
    public void DeleteBatch_WithExclusions_UsesParameterisedJsonbExists()
    {
        Assert.Equal(
            "DELETE FROM \"vestige\".\"wide_events\" WHERE ctid IN (SELECT ctid FROM \"vestige\".\"wide_events\" WHERE timestamp_utc < now() - make_interval(secs => @period_seconds) AND NOT jsonb_exists(payload, @k0) AND NOT jsonb_exists(payload, @k1) LIMIT @batch)",
            PostgresRetentionSql.DeleteBatch("vestige", "wide_events", new RetentionPass(TimeSpan.FromDays(30), ["audit_action", "user.id"])));
    }

    [Fact]
    public void DeleteBatch_QuotesIdentifiers()
    {
        Assert.StartsWith(
            "DELETE FROM \"we\"\"ird\".\"t.x\"",
            PostgresRetentionSql.DeleteBatch("we\"ird", "t.x", new RetentionPass(TimeSpan.FromDays(1), [])));
    }

    [Fact]
    public void LockKey_FollowsIndexPrefixConvention()
    {
        Assert.Equal("vestige_wide_events_retention", PostgresRetentionSql.LockKey("vestige", "wide.events"));
    }
}
