using System.Text;
using Vestige.Retention;

namespace Vestige.Sinks.Postgres;

internal static class PostgresRetentionSql
{
    public static string DeleteBatch(string schema, string table, RetentionPass pass)
    {
        var qualified = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var where = new StringBuilder("timestamp_utc < now() - make_interval(secs => @period_seconds)");
        for (var i = 0; i < pass.KeepWhenPresent.Count; i++)
            where.Append($" AND NOT jsonb_exists(payload, @k{i})");

        return $"DELETE FROM {qualified} WHERE ctid IN (SELECT ctid FROM {qualified} WHERE {where} LIMIT @batch)";
    }

    public static string LockKey(string schema, string table)
        => $"{schema}_{table}".Replace(".", "_", StringComparison.Ordinal) + "_retention";

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
