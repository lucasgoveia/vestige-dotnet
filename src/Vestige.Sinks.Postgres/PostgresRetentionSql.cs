using System.Text;
using Vestige.Retention;

namespace Vestige.Sinks.Postgres;

internal static class PostgresRetentionSql
{
    /// <summary>First key of every Vestige advisory lock ("VEST"), keeping them apart from the application's own.</summary>
    public const int LockNamespace = 0x56455354;

    /// <summary>
    /// Transaction-scoped probe: true when no other instance is purging. Released when the statement ends,
    /// so nothing is left on the session; safe behind transaction-mode poolers such as PgBouncer.
    /// </summary>
    public const string LockProbe = "SELECT pg_try_advisory_xact_lock(@ns, hashtext(@key))";

    /// <summary>
    /// One batch of one pass, guarded by a transaction-scoped advisory lock taken in the same statement:
    /// when another instance holds it, nothing is deleted and the pass ends.
    /// </summary>
    public static string DeleteBatch(string schema, string table, RetentionPass pass)
    {
        var qualified = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var where = new StringBuilder("timestamp_utc < now() - make_interval(secs => @period_seconds)");
        for (var i = 0; i < pass.KeepWhenPresent.Count; i++)
            where.Append($" AND NOT jsonb_exists(payload, @k{i})");

        return "WITH lock AS (SELECT pg_try_advisory_xact_lock(@ns, hashtext(@key)) AS held) " +
               $"DELETE FROM {qualified} WHERE (SELECT held FROM lock) AND ctid = ANY(ARRAY(" +
               $"SELECT ctid FROM {qualified} WHERE {where} LIMIT @batch))";
    }

    /// <summary>Retention lock key from the quoted qualified name, so two tables never share a lock.</summary>
    public static string LockKey(string schema, string table)
        => $"vestige|{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}|retention";

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
