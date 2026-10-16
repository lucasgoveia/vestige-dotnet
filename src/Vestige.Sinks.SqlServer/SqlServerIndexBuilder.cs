using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Vestige.Sinks.SqlServer;

/// <summary>Builds promoted indexes on an existing table without blocking startup or inserts.</summary>
internal sealed class SqlServerIndexBuilder(string connectionString, SqlServerTableLayout layout, ILogger logger)
{
    public async Task BuildAsync(IReadOnlyList<PromotedField> pending, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
            return;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var edition = await ScalarIntAsync(connection, SqlServerSchemaSql.EngineEdition, cancellationToken).ConfigureAwait(false);
        var majorVersion = await ScalarIntAsync(connection, SqlServerSchemaSql.ProductMajorVersion, cancellationToken).ConfigureAwait(false);
        var mode = IndexBuildStrategy.ForServer(edition, majorVersion);

        foreach (var field in pending)
        {
            var name = layout.IndexName(field);
            var ddl = SqlServerSchemaSql.CreateIndexOnline(layout, field, lowPriority: mode == IndexBuildMode.OnlineLowPriority);

            if (mode == IndexBuildMode.Skip)
            {
                logger.LogWarning(
                    "Vestige did not build index {Index}: engine edition {Edition} cannot build it online, and an offline index build would block inserts; run it during a maintenance window: {Ddl}",
                    name, edition, SqlServerSchemaSql.CreateIndex(layout, field, online: false));
                continue;
            }

            try
            {
                await BuildOneAsync(connection, field, name, ddl, edition, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // SqlClient reports a cancelled command as a SqlException; shutdown is not a build failure.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Vestige failed to build index {Index}.", name);
            }
        }
    }

    private async Task BuildOneAsync(
        SqlConnection connection,
        PromotedField field,
        string name,
        string ddl,
        int edition,
        CancellationToken cancellationToken)
    {
        if (!await SqlAppLock.TryAcquireAsync(connection, name, cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("Vestige skipped index {Index}: another instance is building it.", name);
            return;
        }

        try
        {
            if (await IndexExistsAsync(connection, name, cancellationToken).ConfigureAwait(false))
                return;

            logger.LogInformation("Vestige building index {Index} online (engine edition {Edition}).", name, edition);
            var stopwatch = Stopwatch.StartNew();
            await using var command = new SqlCommand(ddl, connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Vestige built index {Index} for [{Column}] in {ElapsedMs} ms.", name, field.Column, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            try
            {
                if (connection.State == ConnectionState.Open)
                    await SqlAppLock.ReleaseAsync(connection, name).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // The session-owned lock is released when the connection closes anyway.
            }
        }
    }

    private async Task<bool> IndexExistsAsync(SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SqlServerSchemaSql.SelectIndexNames + " AND name = @index_name", connection);
        command.Parameters.Add(new SqlParameter("@object_name", SqlDbType.NVarChar, 512) { Value = layout.QualifiedTable });
        command.Parameters.Add(new SqlParameter("@index_name", SqlDbType.NVarChar, 128) { Value = name });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<int> ScalarIntAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
