using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class SqlServerSchemaSqlTests
{
    private static SqlServerTableLayout Layout(Action<SqlServerSinkOptions>? configure = null)
    {
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        configure?.Invoke(options);
        return new SqlServerTableLayout(options);
    }

    [Fact]
    public void CreateTable_WithoutPromotedFields_HasExactly020Columns()
    {
        var sql = SqlServerSchemaSql.CreateTable(Layout());

        Assert.StartsWith("IF OBJECT_ID(N'[dbo].[wide_events]', N'U') IS NULL", sql);
        Assert.Contains("[id] BIGINT IDENTITY(1,1) PRIMARY KEY,", sql);
        Assert.Contains("[payload] NVARCHAR(MAX) NOT NULL", sql);
        Assert.Equal(16, sql.Split('\n').Count(line => line.TrimStart().StartsWith('[')));
    }

    [Fact]
    public void CreateTable_WithPromotedFields_AppendsNullableTypedColumns()
    {
        var sql = SqlServerSchemaSql.CreateTable(Layout(o =>
        {
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
            o.PromotedFields.Add("tenant_id", PromotedType.Guid);
        }));

        Assert.Contains("[payload] NVARCHAR(MAX) NOT NULL,\n        [audit_action] NVARCHAR(128) NULL,\n        [tenant_id] UNIQUEIDENTIFIER NULL\n    );", sql);
    }

    [Fact]
    public void CreateIndex_Online_TargetsColumnThenTimestampDesc()
    {
        var layout = Layout(o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));

        Assert.Equal(
            "CREATE INDEX [dbo_wide_events_audit_action_idx] ON [dbo].[wide_events] ([audit_action], [timestamp_utc] DESC) WITH (ONLINE = ON);",
            SqlServerSchemaSql.CreateIndex(layout, layout.Promoted[0], online: true));
        Assert.Equal(
            "CREATE INDEX [dbo_wide_events_audit_action_idx] ON [dbo].[wide_events] ([audit_action], [timestamp_utc] DESC);",
            SqlServerSchemaSql.CreateIndex(layout, layout.Promoted[0], online: false));
    }

    [Fact]
    public void CreateIndexIfMissing_GuardsOnIndexName()
    {
        var layout = Layout(o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));

        var sql = SqlServerSchemaSql.CreateIndexIfMissing(layout, layout.Promoted[0]);

        Assert.StartsWith(
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'dbo_wide_events_audit_action_idx' AND object_id = OBJECT_ID(N'[dbo].[wide_events]'))",
            sql);
        Assert.EndsWith("([audit_action], [timestamp_utc] DESC);", sql);
    }

    [Fact]
    public void AddColumns_OneAlterWithBoundedLockWait()
    {
        var layout = Layout(o =>
        {
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
            o.PromotedFields.Add("tenant_id", PromotedType.Guid);
        });

        Assert.Equal(
            "SET LOCK_TIMEOUT 5000; ALTER TABLE [dbo].[wide_events] ADD [audit_action] NVARCHAR(128) NULL, [tenant_id] UNIQUEIDENTIFIER NULL;",
            SqlServerSchemaSql.AddColumns(layout, layout.Promoted));
    }

    [Fact]
    public void CreateIndexOnline_LowPriority_WaitsWithoutBlockingWriters()
    {
        var layout = Layout(o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));

        Assert.Equal(
            "CREATE INDEX [dbo_wide_events_audit_action_idx] ON [dbo].[wide_events] ([audit_action], [timestamp_utc] DESC) " +
            "WITH (ONLINE = ON (WAIT_AT_LOW_PRIORITY (MAX_DURATION = 1 MINUTES, ABORT_AFTER_WAIT = SELF)));",
            SqlServerSchemaSql.CreateIndexOnline(layout, layout.Promoted[0], lowPriority: true));
        Assert.Equal(
            "SET LOCK_TIMEOUT 10000; CREATE INDEX [dbo_wide_events_audit_action_idx] ON [dbo].[wide_events] ([audit_action], [timestamp_utc] DESC) WITH (ONLINE = ON);",
            SqlServerSchemaSql.CreateIndexOnline(layout, layout.Promoted[0], lowPriority: false));
    }

    [Fact]
    public void AddColumn_IsNullable()
    {
        var layout = Layout(o => o.PromotedFields.Add("tenant_id", PromotedType.Guid));

        Assert.Equal(
            "ALTER TABLE [dbo].[wide_events] ADD [tenant_id] UNIQUEIDENTIFIER NULL;",
            SqlServerSchemaSql.AddColumn(layout, layout.Promoted[0]));
    }

    [Fact]
    public void Ddl_EscapesQuotesAndBracketsInNames()
    {
        var layout = Layout(o =>
        {
            o.Table = "we]ird'name";
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
        });

        var create = SqlServerSchemaSql.CreateTable(layout);
        var guard = SqlServerSchemaSql.CreateIndexIfMissing(layout, layout.Promoted[0]);

        Assert.StartsWith("IF OBJECT_ID(N'[dbo].[we]]ird''name]', N'U') IS NULL", create);
        Assert.Contains("CREATE TABLE [dbo].[we]]ird'name] (", create);
        Assert.Contains("name = N'dbo_we]ird''name_audit_action_idx'", guard);
        Assert.Contains("CREATE INDEX [dbo_we]]ird'name_audit_action_idx]", guard);
    }
}
