using System.Data;
using Microsoft.Data.SqlClient;

namespace Vestige.Sinks.SqlServer;

/// <summary>Session-owned application locks: released explicitly, or automatically when the connection closes.</summary>
internal static class SqlAppLock
{
    public static async Task<bool> TryAcquireAsync(SqlConnection connection, string resource, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "DECLARE @result int; " +
            "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; " +
            "SELECT @result;",
            connection);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
        var result = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return result >= 0;
    }

    public static async Task ReleaseAsync(SqlConnection connection, string resource)
    {
        await using var command = new SqlCommand(
            "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';", connection);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
