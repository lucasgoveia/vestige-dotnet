using Npgsql;
using NpgsqlTypes;
using Vestige.Retention;

namespace Vestige.Sinks.Postgres;

internal sealed class PostgresRetentionStore(string connectionString, PostgresSinkOptions options) : IRetentionStore
{
    private const int DeleteCommandTimeoutSeconds = 300;
    private readonly string _lockKey = PostgresRetentionSql.LockKey(options.Schema, options.Table);

    public async Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext(@key))", connection);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Text, _lockKey);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                return new Lease(connection, _lockKey);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    public async Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(PostgresRetentionSql.DeleteBatch(options.Schema, options.Table, pass), connection)
        {
            CommandTimeout = DeleteCommandTimeoutSeconds,
        };
        command.Parameters.AddWithValue("batch", NpgsqlDbType.Integer, batchSize);
        command.Parameters.AddWithValue("period_seconds", NpgsqlDbType.Double, pass.Period.TotalSeconds);
        for (var i = 0; i < pass.KeepWhenPresent.Count; i++)
            command.Parameters.AddWithValue($"k{i}", NpgsqlDbType.Text, pass.KeepWhenPresent[i]);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Lease(NpgsqlConnection connection, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext(@key))", connection);
                command.Parameters.AddWithValue("key", NpgsqlDbType.Text, key);
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // Session-level advisory locks are released when the connection closes anyway.
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
