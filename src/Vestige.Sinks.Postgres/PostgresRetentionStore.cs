using Npgsql;
using NpgsqlTypes;
using Vestige.Retention;

namespace Vestige.Sinks.Postgres;

/// <summary>
/// Retention against PostgreSQL. Coordination uses transaction-scoped advisory locks taken inside each
/// statement, never session locks: nothing outlives a statement, so a pooled or PgBouncer-multiplexed
/// backend can never be left holding a lock.
/// </summary>
internal sealed class PostgresRetentionStore(string connectionString, PostgresSinkOptions options) : IRetentionStore
{
    private const int DeleteCommandTimeoutSeconds = 300;
    private static readonly IAsyncDisposable NoLease = new NoopLease();
    private readonly string _lockKey = PostgresRetentionSql.LockKey(options.Schema, options.Table);

    /// <summary>
    /// Probes the lock: skips the cycle when another instance is purging right now. Each batch then takes
    /// the lock again for its own statement, so instances interleave at worst and never delete concurrently.
    /// </summary>
    public async Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(PostgresRetentionSql.LockProbe, connection);
        AddLockParameters(command);
        var free = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return free ? NoLease : null;
    }

    public async Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(PostgresRetentionSql.DeleteBatch(options.Schema, options.Table, pass), connection)
        {
            CommandTimeout = DeleteCommandTimeoutSeconds,
        };
        AddLockParameters(command);
        command.Parameters.AddWithValue("batch", NpgsqlDbType.Integer, batchSize);
        command.Parameters.AddWithValue("period_seconds", NpgsqlDbType.Double, pass.Period.TotalSeconds);
        for (var i = 0; i < pass.KeepWhenPresent.Count; i++)
            command.Parameters.AddWithValue($"k{i}", NpgsqlDbType.Text, pass.KeepWhenPresent[i]);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void AddLockParameters(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("ns", NpgsqlDbType.Integer, PostgresRetentionSql.LockNamespace);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Text, _lockKey);
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
