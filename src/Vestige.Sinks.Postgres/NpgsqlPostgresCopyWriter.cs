using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Vestige.Sinks.Postgres;

internal sealed class NpgsqlPostgresCopyWriter : IPostgresCopyWriter
{
    private NpgsqlDataSource? _dataSource;
    private NpgsqlConnection? _connection;
    private PostgresSinkOptions? _options;

    public async Task InitializeAsync(PostgresSinkOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        _options = options;
        _dataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
        _connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureStorageAsync(CancellationToken cancellationToken)
    {
        var connection = GetConnection();
        var options = _options ?? throw new InvalidOperationException("Sink options have not been initialized.");

        var schema = QuoteIdentifier(options.Schema);
        var table = QuoteIdentifier(options.Table);
        var indexPrefix = $"{options.Schema}_{options.Table}".Replace(".", "_", StringComparison.Ordinal);

        var sql = $"""
            CREATE SCHEMA IF NOT EXISTS {schema};

            CREATE TABLE IF NOT EXISTS {schema}.{table} (
                id bigserial PRIMARY KEY,
                event_id varchar(256) NOT NULL,
                timestamp_utc timestamptz NOT NULL,
                service_name text NULL,
                service_version text NULL,
                deployment_environment text NULL,
                cloud_region text NULL,
                trace_id text NULL,
                span_id text NULL,
                outcome text NULL,
                duration_ms double precision NOT NULL,
                http_status_code integer NULL,
                http_method text NULL,
                http_path text NULL,
                error_type text NULL,
                payload jsonb NOT NULL
            );

            CREATE INDEX IF NOT EXISTS {QuoteIdentifier(indexPrefix + "_event_id_idx")} ON {schema}.{table} (event_id);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier(indexPrefix + "_timestamp_idx")} ON {schema}.{table} (timestamp_utc);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier(indexPrefix + "_service_name_idx")} ON {schema}.{table} (service_name);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier(indexPrefix + "_outcome_idx")} ON {schema}.{table} (outcome);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier(indexPrefix + "_trace_id_idx")} ON {schema}.{table} (trace_id);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier(indexPrefix + "_payload_idx")} ON {schema}.{table} USING GIN (payload);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteSingleAsync(PostgresEventRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var connection = GetConnection();
        var options = _options ?? throw new InvalidOperationException("Sink options have not been initialized.");
        var sql =
            $"INSERT INTO {QuoteIdentifier(options.Schema)}.{QuoteIdentifier(options.Table)} " +
            "(event_id, timestamp_utc, service_name, service_version, deployment_environment, cloud_region, trace_id, span_id, outcome, duration_ms, http_status_code, http_method, http_path, error_type, payload) " +
            "VALUES (@event_id, @timestamp_utc, @service_name, @service_version, @deployment_environment, @cloud_region, @trace_id, @span_id, @outcome, @duration_ms, @http_status_code, @http_method, @http_path, @error_type, @payload)";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Varchar, row.EventId);
        command.Parameters.AddWithValue("timestamp_utc", NpgsqlDbType.TimestampTz, row.TimestampUtc);
        AddNullableParameter(command, "service_name", NpgsqlDbType.Text, row.ServiceName);
        AddNullableParameter(command, "service_version", NpgsqlDbType.Text, row.ServiceVersion);
        AddNullableParameter(command, "deployment_environment", NpgsqlDbType.Text, row.DeploymentEnvironment);
        AddNullableParameter(command, "cloud_region", NpgsqlDbType.Text, row.CloudRegion);
        AddNullableParameter(command, "trace_id", NpgsqlDbType.Text, row.TraceId);
        AddNullableParameter(command, "span_id", NpgsqlDbType.Text, row.SpanId);
        AddNullableParameter(command, "outcome", NpgsqlDbType.Text, row.Outcome);
        command.Parameters.AddWithValue("duration_ms", NpgsqlDbType.Double, row.DurationMs);
        AddNullableParameter(command, "http_status_code", NpgsqlDbType.Integer, row.HttpStatusCode);
        AddNullableParameter(command, "http_method", NpgsqlDbType.Text, row.HttpMethod);
        AddNullableParameter(command, "http_path", NpgsqlDbType.Text, row.HttpPath);
        AddNullableParameter(command, "error_type", NpgsqlDbType.Text, row.ErrorType);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, Encoding.UTF8.GetString(row.PayloadUtf8));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(IReadOnlyList<PostgresEventRow> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            return;

        var connection = GetConnection();
        var options = _options ?? throw new InvalidOperationException("Sink options have not been initialized.");

        var commandText =
            $"COPY {QuoteIdentifier(options.Schema)}.{QuoteIdentifier(options.Table)} " +
            "(event_id, timestamp_utc, service_name, service_version, deployment_environment, cloud_region, trace_id, span_id, outcome, duration_ms, http_status_code, http_method, http_path, error_type, payload) " +
            "FROM STDIN (FORMAT BINARY)";

        await using var importer = await connection.BeginBinaryImportAsync(commandText, cancellationToken).ConfigureAwait(false);

        foreach (var row in batch)
        {
            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(row.EventId, NpgsqlDbType.Varchar, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(row.TimestampUtc, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.ServiceName, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.ServiceVersion, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.DeploymentEnvironment, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.CloudRegion, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.TraceId, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.SpanId, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.Outcome, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(row.DurationMs, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.HttpStatusCode, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.HttpMethod, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.HttpPath, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteNullableAsync(importer, row.ErrorType, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(Encoding.UTF8.GetString(row.PayloadUtf8), NpgsqlDbType.Jsonb, cancellationToken).ConfigureAwait(false);
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        if (_dataSource is not null)
            await _dataSource.DisposeAsync().ConfigureAwait(false);
    }

    private NpgsqlConnection GetConnection()
        => _connection ?? throw new ObjectDisposedException(nameof(NpgsqlPostgresCopyWriter));

    private static async Task WriteNullableAsync<T>(
        NpgsqlBinaryImporter importer,
        T? value,
        NpgsqlDbType dbType,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await importer.WriteAsync(value, dbType, cancellationToken).ConfigureAwait(false);
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void AddNullableParameter<T>(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType dbType,
        T? value)
    {
        command.Parameters.AddWithValue(name, dbType, value is null ? DBNull.Value : value);
    }

    private static void ValidateOptions(PostgresSinkOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("Postgres sink connection string is required.");
        if (string.IsNullOrWhiteSpace(options.Schema))
            throw new InvalidOperationException("Postgres sink schema is required.");
        if (string.IsNullOrWhiteSpace(options.Table))
            throw new InvalidOperationException("Postgres sink table is required.");
    }
}
