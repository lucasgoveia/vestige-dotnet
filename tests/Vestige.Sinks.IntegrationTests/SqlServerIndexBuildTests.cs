using Microsoft.Extensions.Logging;
using Vestige.Sinks.IntegrationTests.Support;
using Vestige.Sinks.SqlServer;

namespace Vestige.Sinks.IntegrationTests;

public sealed class SqlServerIndexBuildTests(SqlServerFixture db)
{
    private static void Promote(SqlServerSinkOptions o)
    {
        o.PromotedFields.Add("audit_action", PromotedType.String(128));
        o.PromotedFields.Add("tenant_id", PromotedType.Guid);
    }

    [Fact]
    public async Task UpgradeFrom020_AddsColumns_ThenBuildsIndexesOnlineInBackground()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        var logger = new ListLogger<SqlServerSink>();
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote, logger);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await sink.EmitAsync(TestEvents.Make(ev => ev.Set("audit_action", "tenant.create")), TestContext.Current.CancellationToken);
        await sink.IndexBuildCompletion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var indexes = await SqlTestDb.IndexNamesAsync(db.ConnectionString, "dbo", table);
        Assert.Contains($"dbo_{table}_audit_action_idx", indexes);
        Assert.Contains($"dbo_{table}_tenant_id_idx", indexes);
        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE event_id = 'legacy-1' AND audit_action IS NULL"));
        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE audit_action = N'tenant.create'"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("online"));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task TwoReplicasUpgradingTogether_BothFinishWithoutErrors()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        var loggerA = new ListLogger<SqlServerSink>();
        var loggerB = new ListLogger<SqlServerSink>();
        await using var a = SinkFactory.SqlServer(db.ConnectionString, table, Promote, loggerA);
        await using var b = SinkFactory.SqlServer(db.ConnectionString, table, Promote, loggerB);

        await Task.WhenAll(
            a.InitializeAsync(TestContext.Current.CancellationToken),
            b.InitializeAsync(TestContext.Current.CancellationToken));
        await Task.WhenAll(a.IndexBuildCompletion, b.IndexBuildCompletion)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var indexes = await SqlTestDb.IndexNamesAsync(db.ConnectionString, "dbo", table);
        Assert.Contains($"dbo_{table}_audit_action_idx", indexes);
        Assert.Contains($"dbo_{table}_tenant_id_idx", indexes);
        Assert.DoesNotContain(loggerA.Entries.Concat(loggerB.Entries), e => e.Level >= LogLevel.Error);
    }
}
