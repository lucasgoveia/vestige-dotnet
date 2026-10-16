using System.Text;

namespace Vestige.Sinks.SqlServer;

/// <summary>Pure DDL and catalog-query text. Nothing here touches a connection.</summary>
internal static class SqlServerSchemaSql
{
    public const string TableExists =
        "SELECT CASE WHEN OBJECT_ID(@object_name, N'U') IS NULL THEN 0 ELSE 1 END";

    public const string SelectColumns = """
        SELECT c.name, t.name, c.max_length, c.scale, c.is_nullable
        FROM sys.columns c
        JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(@object_name, N'U')
        """;

    public const string SelectIndexNames =
        "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(@object_name, N'U') AND name IS NOT NULL";

    public const string EngineEdition = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)";

    public static string CreateSchema(SqlServerSinkOptions options)
    {
        var schemaLiteral = SqlServerTableLayout.Literal(options.Schema);
        var schemaForExec = SqlServerTableLayout.Literal(SqlServerTableLayout.QuoteIdentifier(options.Schema));
        return $"IF SCHEMA_ID(N'{schemaLiteral}') IS NULL EXEC('CREATE SCHEMA {schemaForExec}')";
    }

    public static string CreateTable(SqlServerTableLayout layout)
    {
        var sql = new StringBuilder();
        sql.Append($"IF OBJECT_ID(N'{layout.QualifiedTableLiteral}', N'U') IS NULL\n");
        sql.Append($"    CREATE TABLE {layout.QualifiedTable} (\n");
        sql.Append("        [id] BIGINT IDENTITY(1,1) PRIMARY KEY,\n");
        sql.Append("        [event_id] VARCHAR(256) NOT NULL,\n");
        sql.Append("        [timestamp_utc] DATETIMEOFFSET(7) NOT NULL,\n");
        sql.Append("        [service_name] NVARCHAR(256) NULL,\n");
        sql.Append("        [service_version] NVARCHAR(64) NULL,\n");
        sql.Append("        [deployment_environment] NVARCHAR(128) NULL,\n");
        sql.Append("        [cloud_region] NVARCHAR(128) NULL,\n");
        sql.Append("        [trace_id] NVARCHAR(64) NULL,\n");
        sql.Append("        [span_id] NVARCHAR(32) NULL,\n");
        sql.Append("        [outcome] NVARCHAR(32) NULL,\n");
        sql.Append("        [duration_ms] FLOAT NOT NULL,\n");
        sql.Append("        [http_status_code] INT NULL,\n");
        sql.Append("        [http_method] NVARCHAR(16) NULL,\n");
        sql.Append("        [http_path] NVARCHAR(2048) NULL,\n");
        sql.Append("        [error_type] NVARCHAR(512) NULL,\n");
        sql.Append("        [payload] NVARCHAR(MAX) NOT NULL");
        foreach (var field in layout.Promoted)
            sql.Append($",\n        {SqlServerTableLayout.QuoteIdentifier(field.Column)} {field.Type.SqlTypeName} NULL");
        sql.Append("\n    );");
        return sql.ToString();
    }

    public static string CreateFixedIndexes(SqlServerTableLayout layout)
    {
        var sql = new StringBuilder();
        foreach (var (suffix, column) in new[]
                 {
                     ("event_id_idx", "event_id"),
                     ("timestamp_idx", "timestamp_utc"),
                     ("service_name_idx", "service_name"),
                     ("outcome_idx", "outcome"),
                     ("trace_id_idx", "trace_id"),
                 })
        {
            var name = $"{layout.IndexPrefix}_{suffix}";
            sql.Append(IndexGuard(layout, name));
            sql.Append($"    CREATE INDEX {SqlServerTableLayout.QuoteIdentifier(name)} ON {layout.QualifiedTable} ([{column}]);\n");
        }

        return sql.ToString();
    }

    public static string AddColumn(SqlServerTableLayout layout, PromotedField field)
        => $"ALTER TABLE {layout.QualifiedTable} ADD {SqlServerTableLayout.QuoteIdentifier(field.Column)} {field.Type.SqlTypeName} NULL;";

    public static string CreateIndex(SqlServerTableLayout layout, PromotedField field, bool online)
        => $"CREATE INDEX {SqlServerTableLayout.QuoteIdentifier(layout.IndexName(field))} ON {layout.QualifiedTable} " +
           $"({SqlServerTableLayout.QuoteIdentifier(field.Column)}, [timestamp_utc] DESC)" +
           (online ? " WITH (ONLINE = ON);" : ";");

    public static string CreateIndexIfMissing(SqlServerTableLayout layout, PromotedField field)
        => IndexGuard(layout, layout.IndexName(field)) + "    " + CreateIndex(layout, field, online: false);

    private static string IndexGuard(SqlServerTableLayout layout, string indexName)
        => $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{SqlServerTableLayout.Literal(indexName)}' " +
           $"AND object_id = OBJECT_ID(N'{layout.QualifiedTableLiteral}'))\n";
}
