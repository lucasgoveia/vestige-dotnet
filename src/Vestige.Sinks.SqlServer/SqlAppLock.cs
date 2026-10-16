using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Vestige.Sinks.SqlServer;

/// <summary>
/// Session-owned application locks: released explicitly, or when the session ends. Lock connections are
/// opened unpooled (<see cref="LockConnectionString"/>) so disposing one really ends its session.
/// </summary>
internal static class SqlAppLock
{
    private const int MaxResourceLength = 255;

    /// <summary>
    /// Lock resource for <paramref name="purpose"/> on a table, built from the quoted qualified name so two
    /// tables never share a lock. Names past the 255-character limit are hashed rather than truncated.
    /// </summary>
    public static string Resource(string qualifiedTable, string purpose)
    {
        var name = $"vestige|{qualifiedTable}|{purpose}";
        if (name.Length <= MaxResourceLength)
            return name;

        return "vestige|sha256|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
    }

    /// <summary>The same connection string without pooling: a pooled session would keep its lock after dispose.</summary>
    public static string LockConnectionString(string connectionString)
        => new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    /// <summary>Map <c>sp_getapplock</c>'s return code: granted, busy (timeout or deadlock victim), or a failed call.</summary>
    public static bool Interpret(int result, string resource) => result switch
    {
        >= 0 => true,
        -1 or -3 => false,
        _ => throw new InvalidOperationException(
            $"sp_getapplock for '{resource}' failed with code {result.ToString(CultureInfo.InvariantCulture)}."),
    };

    public static async Task<bool> TryAcquireAsync(SqlConnection connection, string resource, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "DECLARE @result int; " +
            "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; " +
            "SELECT @result;",
            connection);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, MaxResourceLength) { Value = resource });
        var result = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return Interpret(result, resource);
    }

    public static async Task ReleaseAsync(SqlConnection connection, string resource)
    {
        await using var command = new SqlCommand(
            "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';", connection);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, MaxResourceLength) { Value = resource });
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
