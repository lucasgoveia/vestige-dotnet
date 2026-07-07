using System.Data;
using Microsoft.Data.SqlClient;

namespace Vestige.Sinks.SqlServer;

internal sealed class SqlServerWriter : ISqlServerWriter
{
    private SqlConnection? _connection;
    private SqlServerSinkOptions? _options;

    public async Task InitializeAsync(SqlServerSinkOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        _options = options;
        _connection = new SqlConnection(options.ConnectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureStorageAsync(CancellationToken cancellationToken)
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
    }

    public async Task WriteSingleAsync(SqlServerEventRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var options = _options ?? throw new InvalidOperationException("Sink options have not been initialized.");

        var sql =
            $"INSERT INTO {QuoteIdentifier(options.Schema)}.{QuoteIdentifier(options.Table)} " +
            "(event_id, timestamp_utc, service_name, service_version, deployment_environment, cloud_region, trace_id, span_id, outcome, duration_ms, http_status_code, http_method, http_path, error_type, payload) " +
            "VALUES (@event_id, @timestamp_utc, @service_name, @service_version, @deployment_environment, @cloud_region, @trace_id, @span_id, @outcome, @duration_ms, @http_status_code, @http_method, @http_path, @error_type, @payload)";

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

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(IReadOnlyList<SqlServerEventRow> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            return;

        var connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var options = _options ?? throw new InvalidOperationException("Sink options have not been initialized.");

        using var bulkCopy = new SqlBulkCopy(connection);
        bulkCopy.DestinationTableName = $"{QuoteIdentifier(options.Schema)}.{QuoteIdentifier(options.Table)}";
        bulkCopy.ColumnMappings.Add("event_id", "event_id");
        bulkCopy.ColumnMappings.Add("timestamp_utc", "timestamp_utc");
        bulkCopy.ColumnMappings.Add("service_name", "service_name");
        bulkCopy.ColumnMappings.Add("service_version", "service_version");
        bulkCopy.ColumnMappings.Add("deployment_environment", "deployment_environment");
        bulkCopy.ColumnMappings.Add("cloud_region", "cloud_region");
        bulkCopy.ColumnMappings.Add("trace_id", "trace_id");
        bulkCopy.ColumnMappings.Add("span_id", "span_id");
        bulkCopy.ColumnMappings.Add("outcome", "outcome");
        bulkCopy.ColumnMappings.Add("duration_ms", "duration_ms");
        bulkCopy.ColumnMappings.Add("http_status_code", "http_status_code");
        bulkCopy.ColumnMappings.Add("http_method", "http_method");
        bulkCopy.ColumnMappings.Add("http_path", "http_path");
        bulkCopy.ColumnMappings.Add("error_type", "error_type");
        bulkCopy.ColumnMappings.Add("payload", "payload");

        using var dataTable = BuildDataTable(batch);
        await bulkCopy.WriteToServerAsync(dataTable).ConfigureAwait(false);
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

    private static DataTable BuildDataTable(IReadOnlyList<SqlServerEventRow> batch)
    {
        var dt = new DataTable();
        dt.Columns.Add("event_id", typeof(string));
        dt.Columns.Add("timestamp_utc", typeof(DateTimeOffset));
        dt.Columns.Add("service_name", typeof(string));
        dt.Columns.Add("service_version", typeof(string));
        dt.Columns.Add("deployment_environment", typeof(string));
        dt.Columns.Add("cloud_region", typeof(string));
        dt.Columns.Add("trace_id", typeof(string));
        dt.Columns.Add("span_id", typeof(string));
        dt.Columns.Add("outcome", typeof(string));
        dt.Columns.Add("duration_ms", typeof(double));
        dt.Columns.Add("http_status_code", typeof(int));
        dt.Columns.Add("http_method", typeof(string));
        dt.Columns.Add("http_path", typeof(string));
        dt.Columns.Add("error_type", typeof(string));
        dt.Columns.Add("payload", typeof(string));

        foreach (var row in batch)
        {
            dt.Rows.Add(
                row.EventId,
                row.TimestampUtc,
                (object?)row.ServiceName ?? DBNull.Value,
                (object?)row.ServiceVersion ?? DBNull.Value,
                (object?)row.DeploymentEnvironment ?? DBNull.Value,
                (object?)row.CloudRegion ?? DBNull.Value,
                (object?)row.TraceId ?? DBNull.Value,
                (object?)row.SpanId ?? DBNull.Value,
                (object?)row.Outcome ?? DBNull.Value,
                row.DurationMs,
                (object?)row.HttpStatusCode ?? DBNull.Value,
                (object?)row.HttpMethod ?? DBNull.Value,
                (object?)row.HttpPath ?? DBNull.Value,
                (object?)row.ErrorType ?? DBNull.Value,
                row.PayloadJson);
        }

        return dt;
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
