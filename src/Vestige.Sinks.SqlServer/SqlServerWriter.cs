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
        var layout = _layout ?? throw new InvalidOperationException("Sink layout has not been initialized.");

        await ExecuteAsync(connection, SqlServerSchemaSql.CreateSchema(options), cancellationToken).ConfigureAwait(false);

        var tableExisted = await TableExistsAsync(connection, layout, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlServerSchemaSql.CreateTable(layout), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlServerSchemaSql.CreateFixedIndexes(layout), cancellationToken).ConfigureAwait(false);

        if (!tableExisted)
            return await CreateIndexesOnNewTableAsync(connection, layout, cancellationToken).ConfigureAwait(false);

        var diff = await ReadDiffAsync(connection, layout, cancellationToken).ConfigureAwait(false);
        if (diff.MissingColumns.Count > 0)
            diff = await AddMissingColumnsAsync(connection, layout, diff, cancellationToken).ConfigureAwait(false);

        return diff.MissingIndexes;
    }

    /// <summary>
    /// Empty table: building inline is instant. Each build takes the same app lock as the background builder;
    /// an index whose lock another instance holds is left to that instance and returned as still pending.
    /// </summary>
    private static async Task<IReadOnlyList<PromotedField>> CreateIndexesOnNewTableAsync(
        SqlConnection connection,
        SqlServerTableLayout layout,
        CancellationToken cancellationToken)
    {
        var pending = new List<PromotedField>();
        foreach (var field in layout.Promoted.Where(f => f.Indexed))
        {
            var lockName = layout.IndexLockName(field);
            if (!await SqlAppLock.TryAcquireAsync(connection, lockName, cancellationToken).ConfigureAwait(false))
            {
                pending.Add(field);
                continue;
            }

            try
            {
                await ExecuteAsync(connection, SqlServerSchemaSql.CreateIndexIfMissing(layout, field), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await ReleaseQuietlyAsync(connection, lockName).ConfigureAwait(false);
            }
        }

        return pending;
    }

    private static async Task ReleaseQuietlyAsync(SqlConnection connection, string lockName)
    {
        try
        {
            if (connection.State == ConnectionState.Open)
                await SqlAppLock.ReleaseAsync(connection, lockName).ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // A failed release means a broken connection, which is never returned to the pool.
        }
    }

    private const int AddColumnsAttempts = 3;
    private static readonly TimeSpan AddColumnsRetryPause = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The ALTER is metadata-only but needs a schema-modification lock; while it waits, every insert queues
    /// behind it. Each attempt waits at most <see cref="SqlServerSchemaSql.AddColumnsLockTimeoutMs"/>, with
    /// pauses between attempts that let queued writes drain.
    /// </summary>
    private static async Task<SchemaDiff> AddMissingColumnsAsync(
        SqlConnection connection,
        SqlServerTableLayout layout,
        SchemaDiff diff,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await ExecuteAsync(connection, SqlServerSchemaSql.AddColumns(layout, diff.MissingColumns), cancellationToken).ConfigureAwait(false);
                    return diff;
                }
                catch (SqlException ex) when (ex.Number is 1222 or 2705 && attempt < AddColumnsAttempts)
                {
                    // 1222: lock wait timed out. 2705: another replica added a column first.
                    await Task.Delay(AddColumnsRetryPause, cancellationToken).ConfigureAwait(false);
                }
                catch (SqlException ex) when (ex.Number == 1222)
                {
                    throw new InvalidOperationException(
                        $"Could not add promoted column(s) to {layout.QualifiedTable}: waited {AddColumnsAttempts} times for a schema lock " +
                        $"({SqlServerSchemaSql.AddColumnsLockTimeoutMs} ms each) while another session held a lock on the table. " +
                        "Restart once the blocking session has finished.",
                        ex);
                }

                diff = await ReadDiffAsync(connection, layout, cancellationToken).ConfigureAwait(false);
                if (diff.MissingColumns.Count == 0)
                    return diff;
            }
        }
        finally
        {
            // SET LOCK_TIMEOUT is session-scoped; never leave it on the connection inserts use. A failure here
            // means the connection is broken (and will not be reused), so it must not mask the original error.
            try
            {
                if (connection.State == ConnectionState.Open)
                    await ExecuteAsync(connection, "SET LOCK_TIMEOUT -1;", CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqlException)
            {
            }
        }
    }

    public async Task VerifyStorageAsync(CancellationToken cancellationToken)
    {
        var layout = _layout ?? throw new InvalidOperationException("Sink layout has not been initialized.");
        if (layout.Promoted.Count == 0)
            return;

        var connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var diff = await ReadDiffAsync(connection, layout, cancellationToken).ConfigureAwait(false);
        diff.RequireNoMissingColumns();
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, SqlServerTableLayout layout, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SqlServerSchemaSql.TableExists, connection);
        command.Parameters.Add(new SqlParameter("@object_name", SqlDbType.NVarChar, 512) { Value = layout.QualifiedTable });
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    private static async Task<SchemaDiff> ReadDiffAsync(SqlConnection connection, SqlServerTableLayout layout, CancellationToken cancellationToken)
    {
        var columns = new List<ExistingColumn>();
        await using (var command = new SqlCommand(SqlServerSchemaSql.SelectColumns, connection))
        {
            command.Parameters.Add(new SqlParameter("@object_name", SqlDbType.NVarChar, 512) { Value = layout.QualifiedTable });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(new ExistingColumn(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    reader.GetByte(3),
                    reader.GetBoolean(4)));
            }
        }

        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand(SqlServerSchemaSql.SelectIndexNames, connection))
        {
            command.Parameters.Add(new SqlParameter("@object_name", SqlDbType.NVarChar, 512) { Value = layout.QualifiedTable });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                indexes.Add(reader.GetString(0));
        }

        return SchemaDiff.Compute(layout, columns, indexes);
    }

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
