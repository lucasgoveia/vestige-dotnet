using System.Data;
using Microsoft.Data.SqlClient;
using Vestige.Retention;

namespace Vestige.Sinks.SqlServer;

internal sealed class SqlServerRetentionStore(string connectionString, SqlServerTableLayout layout) : IRetentionStore
{
    private const int DeleteCommandTimeoutSeconds = 300;

    public async Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (await SqlAppLock.TryAcquireAsync(connection, layout.RetentionLockName, cancellationToken).ConfigureAwait(false))
                return new Lease(connection, layout.RetentionLockName);
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
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SqlServerRetentionSql.DeleteBatch(layout, pass), connection)
        {
            CommandTimeout = DeleteCommandTimeoutSeconds,
        };
        command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batchSize });
        command.Parameters.Add(new SqlParameter("@period_seconds", SqlDbType.Int) { Value = checked((int)pass.Period.TotalSeconds) });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Lease(SqlConnection connection, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (connection.State == ConnectionState.Open)
                    await SqlAppLock.ReleaseAsync(connection, resource).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // Closing the connection releases the session-owned lock anyway.
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
