using System.Data;
using Microsoft.Data.SqlClient;
using Vestige.Sinks.SqlServer;

namespace Vestige.Tests;

public sealed class SqlServerTableLayoutTests
{
    private const string FixedInsert020 =
        "INSERT INTO [dbo].[wide_events] " +
        "(event_id, timestamp_utc, service_name, service_version, deployment_environment, cloud_region, trace_id, span_id, outcome, duration_ms, http_status_code, http_method, http_path, error_type, payload) " +
        "VALUES (@event_id, @timestamp_utc, @service_name, @service_version, @deployment_environment, @cloud_region, @trace_id, @span_id, @outcome, @duration_ms, @http_status_code, @http_method, @http_path, @error_type, @payload)";

    private static SqlServerSinkOptions Options(Action<SqlServerSinkOptions>? configure = null)
    {
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void InsertSql_WithoutPromotedFields_IsIdenticalTo020()
    {
        var layout = new SqlServerTableLayout(Options());

        Assert.Equal(FixedInsert020, layout.InsertSql);
        Assert.Equal(15, layout.InsertColumns.Count);
    }

    [Fact]
    public void InsertSql_WithPromotedFields_AppendsQuotedColumnsAndIndexedParameters()
    {
        var layout = new SqlServerTableLayout(Options(o =>
        {
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
            o.PromotedFields.Add("tenant_id", PromotedType.Guid);
        }));

        Assert.EndsWith(
            "error_type, payload, [audit_action], [tenant_id]) VALUES (@event_id, @timestamp_utc, @service_name, @service_version, @deployment_environment, @cloud_region, @trace_id, @span_id, @outcome, @duration_ms, @http_status_code, @http_method, @http_path, @error_type, @payload, @promoted_0, @promoted_1)",
            layout.InsertSql);
        Assert.Equal(new[] { "audit_action", "tenant_id" }, layout.InsertColumns.TakeLast(2));
    }

    [Fact]
    public void Names_SanitizeAndQuoteUnusualTableNames()
    {
        var layout = new SqlServerTableLayout(Options(o =>
        {
            o.Schema = "ops";
            o.Table = "wide.events]x'y";
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
        }));

        Assert.Equal("[ops].[wide.events]]x'y]", layout.QualifiedTable);
        Assert.Equal("[ops].[wide.events]]x''y]", layout.QualifiedTableLiteral);
        Assert.Equal("ops_wide_events]x'y", layout.IndexPrefix);
        Assert.Equal("ops_wide_events]x'y_audit_action_idx", layout.IndexName(layout.Promoted[0]));
        Assert.Equal(SqlAppLock.Resource("[ops].[wide.events]]x'y]", "retention"), layout.RetentionLockName);
        Assert.Equal(SqlAppLock.Resource("[ops].[wide.events]]x'y]", "index|audit_action"), layout.IndexLockName(layout.Promoted[0]));
    }

    [Fact]
    public void ColumnFor_ReturnsColumnOfPromotedKey()
    {
        var layout = new SqlServerTableLayout(Options(o => o.PromotedFields.Add("user.id", PromotedType.String(64))));

        Assert.Equal("user_id", layout.ColumnFor("user.id"));
        Assert.Throws<KeyNotFoundException>(() => layout.ColumnFor("missing"));
    }

    [Fact]
    public void DataTable_HasFixedThenPromotedColumns_AndRowsUseDbNull()
    {
        var tenant = Guid.NewGuid();
        var layout = new SqlServerTableLayout(Options(o =>
        {
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
            o.PromotedFields.Add("tenant_id", PromotedType.Guid);
        }));
        var row = new SqlServerEventRow
        {
            EventId = "e1",
            TimestampUtc = DateTimeOffset.UtcNow,
            DurationMs = 1,
            PayloadJson = "{}",
            PromotedValues = [null, tenant],
        };

        using var table = layout.CreateDataTable();
        layout.AddRow(table, row);

        Assert.Equal(layout.InsertColumns, table.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        Assert.Equal(typeof(Guid), table.Columns["tenant_id"]!.DataType);
        Assert.Equal(DBNull.Value, table.Rows[0]["audit_action"]);
        Assert.Equal(tenant, table.Rows[0]["tenant_id"]);
    }

    [Fact]
    public void AddPromotedParameters_TypesAndSizesParameters()
    {
        var layout = new SqlServerTableLayout(Options(o =>
        {
            o.PromotedFields.Add("audit_action", PromotedType.String(128));
            o.PromotedFields.Add("tenant_id", PromotedType.Guid);
        }));
        var row = new SqlServerEventRow
        {
            EventId = "e1",
            TimestampUtc = DateTimeOffset.UtcNow,
            DurationMs = 1,
            PayloadJson = "{}",
            PromotedValues = ["tenant.create", null],
        };
        using var command = new SqlCommand();

        layout.AddPromotedParameters(command, row);

        Assert.Equal(SqlDbType.NVarChar, command.Parameters["@promoted_0"].SqlDbType);
        Assert.Equal(128, command.Parameters["@promoted_0"].Size);
        Assert.Equal("tenant.create", command.Parameters["@promoted_0"].Value);
        Assert.Equal(SqlDbType.UniqueIdentifier, command.Parameters["@promoted_1"].SqlDbType);
        Assert.Equal(DBNull.Value, command.Parameters["@promoted_1"].Value);
    }
}
