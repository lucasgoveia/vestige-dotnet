using System.Text;
using Vestige.Retention;

namespace Vestige.Sinks.SqlServer;

internal static class SqlServerRetentionSql
{
    /// <summary>
    /// One batch of one pass. The timestamp range is served by the timestamp index; the
    /// <c>IS NULL</c> checks are residual predicates on promoted columns.
    /// </summary>
    public static string DeleteBatch(SqlServerTableLayout layout, RetentionPass pass)
    {
        var sql = new StringBuilder(
            $"DELETE TOP (@batch) FROM {layout.QualifiedTable} WHERE [timestamp_utc] < DATEADD(SECOND, -@period_seconds, SYSDATETIMEOFFSET())");
        foreach (var key in pass.KeepWhenPresent)
            sql.Append($" AND {SqlServerTableLayout.QuoteIdentifier(layout.ColumnFor(key))} IS NULL");
        return sql.ToString();
    }
}
