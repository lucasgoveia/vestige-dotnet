using Microsoft.Data.SqlClient;
using Vestige.Sinks.IntegrationTests.Support;
using Vestige.Sinks.SqlServer;

namespace Vestige.Sinks.IntegrationTests;

public sealed class SqlServerRetentionTests(SqlServerFixture db)
{
    private static void Configure(SqlServerSinkOptions o)
    {
        o.PromotedFields.Add("audit_action", PromotedType.String(128));
        o.Retention.Default = TimeSpan.FromDays(30);
        o.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));
    }

    private async Task SeedAsync(SqlServerSink sink, string table)
    {
        await sink.EmitBatchAsync(
        [
            TestEvents.Make(ev => ev.Set("seed", "telemetry_old")),
            TestEvents.Make(ev => ev.Set("seed", "telemetry_new")),
            TestEvents.Make(ev => { ev.Set("seed", "audit_mid"); ev.Set("audit_action", "tenant.create"); }),
            TestEvents.Make(ev => { ev.Set("seed", "audit_old"); ev.Set("audit_action", "tenant.remove"); }),
        ], TestContext.Current.CancellationToken);

        await SqlTestDb.ExecuteAsync(db.ConnectionString, $"""
            UPDATE dbo.[{table}] SET timestamp_utc = DATEADD(DAY, -40, SYSDATETIMEOFFSET()) WHERE JSON_VALUE(payload, '$.seed') = 'telemetry_old';
            UPDATE dbo.[{table}] SET timestamp_utc = DATEADD(DAY, -1, SYSDATETIMEOFFSET()) WHERE JSON_VALUE(payload, '$.seed') = 'telemetry_new';
            UPDATE dbo.[{table}] SET timestamp_utc = DATEADD(DAY, -100, SYSDATETIMEOFFSET()) WHERE JSON_VALUE(payload, '$.seed') = 'audit_mid';
            UPDATE dbo.[{table}] SET timestamp_utc = DATEADD(DAY, -400, SYSDATETIMEOFFSET()) WHERE JSON_VALUE(payload, '$.seed') = 'audit_old';
            """);
    }

    [Fact]
    public async Task Cycle_KeepsAuditLongerThanTelemetry()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Configure);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedAsync(sink, table);

        var result = await sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Acquired);
        Assert.Equal(new[] { 1L, 1L }, result.DeletedPerPass);
        Assert.Equal("audit_mid,telemetry_new", await SqlTestDb.ScalarAsync<string>(db.ConnectionString,
            $"SELECT STRING_AGG(JSON_VALUE(payload, '$.seed'), ',') WITHIN GROUP (ORDER BY JSON_VALUE(payload, '$.seed')) FROM dbo.[{table}]"));
    }

    [Fact]
    public async Task Cycle_LockHeldByAnotherInstance_Skips()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Configure);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedAsync(sink, table);
        await using var holder = new SqlConnection(db.ConnectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        Assert.True(await SqlAppLock.TryAcquireAsync(holder, $"dbo_{table}_retention", TestContext.Current.CancellationToken));

        var result = await sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Acquired);
        Assert.Equal(4, await SqlTestDb.ScalarAsync<int>(db.ConnectionString, $"SELECT COUNT(*) FROM dbo.[{table}]"));
    }
}
