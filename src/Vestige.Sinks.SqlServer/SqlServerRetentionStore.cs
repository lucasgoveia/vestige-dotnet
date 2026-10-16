using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Vestige.Retention;

namespace Vestige.Sinks.SqlServer;

/// <summary>
/// Retention against SQL Server. The lock is a session app lock on an unpooled connection, and every
/// delete of the cycle runs on that same session: the session never sits idle long enough to be dropped
/// by a gateway or NAT, and if it is lost anyway, the deletes fail instead of running unguarded.
/// </summary>
internal sealed class SqlServerRetentionStore(string connectionString, SqlServerTableLayout layout) : IRetentionStore
{
    private const int DeleteCommandTimeoutSeconds = 300;
    private Lease? _active;

    /// <summary>Session id (<c>@@SPID</c>) holding the retention lock while a lease is open.</summary>
    internal int? ActiveSessionId => _active?.SessionId;

    public async Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(SqlAppLock.LockConnectionString(connectionString));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (await SqlAppLock.TryAcquireAsync(connection, layout.RetentionLockName, cancellationToken).ConfigureAwait(false))
            {
                await using var spid = new SqlCommand("SELECT @@SPID", connection);
                var sessionId = Convert.ToInt32(await spid.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                _active = new Lease(this, connection, layout.RetentionLockName, sessionId);
                return _active;
            }
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
        var connection = _active?.Connection
            ?? throw new InvalidOperationException("Retention deletes must run while the retention lock is held.");

        await using var command = new SqlCommand(SqlServerRetentionSql.DeleteBatch(layout, pass), connection)
        {
            CommandTimeout = DeleteCommandTimeoutSeconds,
        };
        command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batchSize });
        command.Parameters.Add(new SqlParameter("@period_seconds", SqlDbType.Int) { Value = checked((int)pass.Period.TotalSeconds) });
        for (var i = 0; i < pass.KeepWhenPresent.Count; i++)
            command.Parameters.Add(new SqlParameter($"@path_{i}", SqlDbType.NVarChar, 4000) { Value = SqlServerRetentionSql.JsonPath(pass.KeepWhenPresent[i]) });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Lease(SqlServerRetentionStore store, SqlConnection connection, string resource, int sessionId) : IAsyncDisposable
    {
        public SqlConnection Connection => connection;

        public int SessionId => sessionId;

        public async ValueTask DisposeAsync()
        {
            if (ReferenceEquals(store._active, this))
                store._active = null;

            try
            {
                if (connection.State == ConnectionState.Open)
                    await SqlAppLock.ReleaseAsync(connection, resource).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // The connection is unpooled, so disposing it below ends the session and its lock.
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
