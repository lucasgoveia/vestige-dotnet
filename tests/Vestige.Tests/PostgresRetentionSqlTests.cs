using Vestige.Retention;
using Vestige.Sinks.Postgres;

namespace Vestige.Tests;

public sealed class PostgresRetentionSqlTests
{
    [Fact]
    public void DeleteBatch_WithoutExclusions()
    {
        Assert.Equal(
            "WITH lock AS (SELECT pg_try_advisory_xact_lock(@ns, hashtext(@key)) AS held) " +
            "DELETE FROM \"vestige\".\"wide_events\" WHERE (SELECT held FROM lock) AND ctid = ANY(ARRAY(" +
            "SELECT ctid FROM \"vestige\".\"wide_events\" WHERE timestamp_utc < now() - make_interval(secs => @period_seconds) LIMIT @batch))",
            PostgresRetentionSql.DeleteBatch("vestige", "wide_events", new RetentionPass(TimeSpan.FromDays(365), [])));
    }

    [Fact]
    public void DeleteBatch_WithExclusions_UsesParameterisedJsonbExists()
    {
        Assert.Equal(
            "WITH lock AS (SELECT pg_try_advisory_xact_lock(@ns, hashtext(@key)) AS held) " +
            "DELETE FROM \"vestige\".\"wide_events\" WHERE (SELECT held FROM lock) AND ctid = ANY(ARRAY(" +
            "SELECT ctid FROM \"vestige\".\"wide_events\" WHERE timestamp_utc < now() - make_interval(secs => @period_seconds) " +
            "AND NOT jsonb_exists(payload, @k0) AND NOT jsonb_exists(payload, @k1) LIMIT @batch))",
            PostgresRetentionSql.DeleteBatch("vestige", "wide_events", new RetentionPass(TimeSpan.FromDays(30), ["audit_action", "user.id"])));
    }

    [Fact]
    public void DeleteBatch_QuotesIdentifiers()
    {
        Assert.Contains(
            "DELETE FROM \"we\"\"ird\".\"t.x\"",
            PostgresRetentionSql.DeleteBatch("we\"ird", "t.x", new RetentionPass(TimeSpan.FromDays(1), [])));
    }

    [Fact]
    public void LockKey_IsInjectiveForTablesWhoseUnderscoredPrefixesCollide()
    {
        Assert.NotEqual(PostgresRetentionSql.LockKey("vestige", "wide.events"), PostgresRetentionSql.LockKey("vestige", "wide_events"));
        Assert.NotEqual(PostgresRetentionSql.LockKey("a_b", "c"), PostgresRetentionSql.LockKey("a", "b_c"));
        Assert.Equal("vestige|\"vestige\".\"wide_events\"|retention", PostgresRetentionSql.LockKey("vestige", "wide_events"));
    }

    [Fact]
    public void LockProbe_IsTransactionScopedAndNamespaced()
    {
        Assert.Equal("SELECT pg_try_advisory_xact_lock(@ns, hashtext(@key))", PostgresRetentionSql.LockProbe);
        Assert.NotEqual(0, PostgresRetentionSql.LockNamespace);
    }
}
