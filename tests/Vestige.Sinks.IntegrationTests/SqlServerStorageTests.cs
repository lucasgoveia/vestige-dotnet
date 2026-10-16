using Vestige.Sinks.IntegrationTests.Support;
using Vestige.Sinks.SqlServer;

namespace Vestige.Sinks.IntegrationTests;

public sealed class SqlServerStorageTests(SqlServerFixture db)
{
    private static readonly Guid Tenant = Guid.Parse("6f1c2a5e-1d2b-4c3d-9e8f-0a1b2c3d4e5f");

    private static void Promote(SqlServerSinkOptions o)
    {
        o.PromotedFields.Add("audit_action", PromotedType.String(128));
        o.PromotedFields.Add("tenant_id", PromotedType.Guid);
    }

    [Fact]
    public async Task NoPromotedFields_CreatesSameColumnsAs020()
    {
        db.SkipIfUnavailable();
        var legacy = SqlTestDb.UniqueTable();
        var current = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", legacy);
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, current);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            await SqlTestDb.ColumnsAsync(db.ConnectionString, "dbo", legacy),
            await SqlTestDb.ColumnsAsync(db.ConnectionString, "dbo", current));
    }

    [Fact]
    public async Task NewTable_CreatesPromotedColumnsAndIndexesInline()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        var columns = await SqlTestDb.ColumnsAsync(db.ConnectionString, "dbo", table);
        Assert.Equal("nvarchar NULL", columns["audit_action"]);
        Assert.Equal("uniqueidentifier NULL", columns["tenant_id"]);
        var indexes = await SqlTestDb.IndexNamesAsync(db.ConnectionString, "dbo", table);
        Assert.Contains($"dbo_{table}_audit_action_idx", indexes);
        Assert.Contains($"dbo_{table}_tenant_id_idx", indexes);
    }

    [Fact]
    public async Task SingleInsertAndBulkCopy_PersistPromotedValues()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await sink.EmitAsync(TestEvents.Make(ev =>
        {
            ev.Set("audit_action", "tenant.create");
            ev.Set("tenant_id", Tenant);
        }), TestContext.Current.CancellationToken);
        await sink.EmitBatchAsync(
        [
            TestEvents.Make(ev => ev.Set("audit_action", "api_key.revoke")),
            TestEvents.Make(ev => ev.Set("tenant_id", Tenant.ToString())),
        ], TestContext.Current.CancellationToken);

        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE audit_action = N'tenant.create' AND tenant_id = '{Tenant}'"));
        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE audit_action = N'api_key.revoke' AND tenant_id IS NULL"));
        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE audit_action IS NULL AND tenant_id = '{Tenant}'"));
    }

    [Fact]
    public async Task ExistingColumnOfDifferentType_InitializeThrows()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        await SqlTestDb.ExecuteAsync(db.ConnectionString, $"ALTER TABLE dbo.[{table}] ADD tenant_id INT NULL");
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Contains("'tenant_id'", ex.Message);
    }

    [Fact]
    public async Task ExistingNotNullColumn_InitializeThrows()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        await SqlTestDb.ExecuteAsync(db.ConnectionString,
            $"ALTER TABLE dbo.[{table}] ADD tenant_id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID()");
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Contains("NOT NULL", ex.Message);
    }

    [Fact]
    public async Task AutoCreateDisabled_MissingPromotedColumn_InitializeThrows()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, o =>
        {
            Promote(o);
            o.AutoCreate = false;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Contains("AutoCreate is disabled", ex.Message);
        Assert.DoesNotContain("audit_action", (await SqlTestDb.ColumnsAsync(db.ConnectionString, "dbo", table)).Keys);
    }

    [Fact]
    public async Task ExistingTable_AddsMissingColumnsAsNullable()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await Legacy020Schema.CreateAsync(db.ConnectionString, "dbo", table);
        await using var sink = SinkFactory.SqlServer(db.ConnectionString, table, Promote);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        var columns = await SqlTestDb.ColumnsAsync(db.ConnectionString, "dbo", table);
        Assert.Equal("nvarchar NULL", columns["audit_action"]);
        Assert.Equal("uniqueidentifier NULL", columns["tenant_id"]);
        Assert.Equal(1, await SqlTestDb.ScalarAsync<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE event_id = 'legacy-1' AND audit_action IS NULL"));
    }
}
