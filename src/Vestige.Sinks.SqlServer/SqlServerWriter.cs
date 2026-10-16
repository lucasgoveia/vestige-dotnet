using System.Data;
using Microsoft.Data.SqlClient;

namespace Vestige.Sinks.SqlServer;

internal sealed class SqlServerWriter : ISqlServerWriter
{
    private SqlConnection? _connection;
    private SqlServerSinkOptions? _options;
    private SqlServerTableLayout? _layout;

    public async Task InitializeAsync(SqlServerSinkOptions options, SqlServerTableLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(layout);
        ValidateOptions(options);

        _options = options;
        _layout = layout;
        _connection = new SqlConnection(options.ConnectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromotedField>> EnsureStorageAsync(CancellationToken cancellationToken)
    {
        var connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var options = _options ?? throw new InvalidOperationException("Sink options have not been initialized.");

        var schema = QuoteIdentifier(options.Schema);
        var table = QuoteIdentifier(options.Table);
        var schemaForSchemaId = options.Schema.Replace("'", "''", StringComparison.Ordinal);
        var schemaForExec = "[" + options.Schema.Replace("]", "]]", StringComparison.Ordinal) + "]";
        var indexPrefix = $"{options.Schema}_{options.Table}".Replace(".", "_", StringComparison.Ordinal);

        await using (var cmd = new SqlCommand(
            $"IF SCHEMA_ID(N'{schemaForSchemaId}') IS NULL EXEC('CREATE SCHEMA {schemaForExec}')",
            connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cmd = new SqlCommand($"""
            IF OBJECT_ID(N'{schema}.{table}', N'U') IS NULL
                CREATE TABLE {schema}.{table} (
                    [id] BIGINT IDENTITY(1,1) PRIMARY KEY,
                    [event_id] VARCHAR(256) NOT NULL,
                    [timestamp_utc] DATETIMEOFFSET(7) NOT NULL,
                    [service_name] NVARCHAR(256) NULL,
                    [service_version] NVARCHAR(64) NULL,
                    [deployment_environment] NVARCHAR(128) NULL,
                    [cloud_region] NVARCHAR(128) NULL,
                    [trace_id] NVARCHAR(64) NULL,
                    [span_id] NVARCHAR(32) NULL,
                    [outcome] NVARCHAR(32) NULL,
                    [duration_ms] FLOAT NOT NULL,
                    [http_status_code] INT NULL,
                    [http_method] NVARCHAR(16) NULL,
                    [http_path] NVARCHAR(2048) NULL,
                    [error_type] NVARCHAR(512) NULL,
                    [payload] NVARCHAR(MAX) NOT NULL
                );
            """, connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cmd = new SqlCommand($"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexPrefix}_event_id_idx' AND object_id = OBJECT_ID(N'{schema}.{table}'))
                CREATE INDEX [{indexPrefix}_event_id_idx] ON {schema}.{table} ([event_id]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexPrefix}_timestamp_idx' AND object_id = OBJECT_ID(N'{schema}.{table}'))
                CREATE INDEX [{indexPrefix}_timestamp_idx] ON {schema}.{table} ([timestamp_utc]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexPrefix}_service_name_idx' AND object_id = OBJECT_ID(N'{schema}.{table}'))
                CREATE INDEX [{indexPrefix}_service_name_idx] ON {schema}.{table} ([service_name]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexPrefix}_outcome_idx' AND object_id = OBJECT_ID(N'{schema}.{table}'))
                CREATE INDEX [{indexPrefix}_outcome_idx] ON {schema}.{table} ([outcome]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexPrefix}_trace_id_idx' AND object_id = OBJECT_ID(N'{schema}.{table}'))
                CREATE INDEX [{indexPrefix}_trace_id_idx] ON {schema}.{table} ([trace_id]);
            """, connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return [];
    }

    public Task VerifyStorageAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task WriteSingleAsync(SqlServerEventRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var layout = _layout ?? throw new InvalidOperationException("Sink layout has not been initialized.");
        var sql = layout.InsertSql;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@event_id", SqlDbType.VarChar, 256) { Value = row.EventId });
        command.Parameters.Add(new SqlParameter("@timestamp_utc", SqlDbType.DateTimeOffset) { Value = row.TimestampUtc });
        AddNullableStringParameter(command, "@service_name", row.ServiceName);
        AddNullableStringParameter(command, "@service_version", row.ServiceVersion);
        AddNullableStringParameter(command, "@deployment_environment", row.DeploymentEnvironment);
        AddNullableStringParameter(command, "@cloud_region", row.CloudRegion);
        AddNullableStringParameter(command, "@trace_id", row.TraceId);
        AddNullableStringParameter(command, "@span_id", row.SpanId);
        AddNullableStringParameter(command, "@outcome", row.Outcome);
        command.Parameters.Add(new SqlParameter("@duration_ms", SqlDbType.Float) { Value = row.DurationMs });
        AddNullableIntParameter(command, "@http_status_code", row.HttpStatusCode);
        AddNullableStringParameter(command, "@http_method", row.HttpMethod);
        AddNullableStringParameter(command, "@http_path", row.HttpPath);
        AddNullableStringParameter(command, "@error_type", row.ErrorType);
        command.Parameters.Add(new SqlParameter("@payload", SqlDbType.NVarChar, -1) { Value = row.PayloadJson });
        layout.AddPromotedParameters(command, row);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(IReadOnlyList<SqlServerEventRow> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            return;

        var connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var layout = _layout ?? throw new InvalidOperationException("Sink layout has not been initialized.");

        using var bulkCopy = new SqlBulkCopy(connection);
        bulkCopy.DestinationTableName = layout.QualifiedTable;
        foreach (var column in layout.InsertColumns)
            bulkCopy.ColumnMappings.Add(column, column);

        using var dataTable = layout.CreateDataTable();
        foreach (var row in batch)
            layout.AddRow(dataTable, row);

        await bulkCopy.WriteToServerAsync(dataTable, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<SqlConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new ObjectDisposedException(nameof(SqlServerWriter));

        if (connection.State == ConnectionState.Broken)
            connection.Close();

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    private static void AddNullableStringParameter(SqlCommand command, string name, string? value)
        => command.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, -1) { Value = (object?)value ?? DBNull.Value });

    private static void AddNullableIntParameter(SqlCommand command, string name, int? value)
        => command.Parameters.Add(new SqlParameter(name, SqlDbType.Int) { Value = (object?)value ?? DBNull.Value });

    private static string QuoteIdentifier(string identifier)
        => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static void ValidateOptions(SqlServerSinkOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("SQL Server sink connection string is required.");
        if (string.IsNullOrWhiteSpace(options.Schema))
            throw new InvalidOperationException("SQL Server sink schema is required.");
        if (string.IsNullOrWhiteSpace(options.Table))
            throw new InvalidOperationException("SQL Server sink table is required.");
    }
}
