using System.Text;
using Vestige.Retention;

namespace Vestige.Sinks.SqlServer;

internal static class SqlServerRetentionSql
{
    /// <summary>
    /// One batch of one pass. The timestamp range is served by the timestamp index; the keep checks are
    /// residual predicates. A row is kept when its promoted column is set or, for rows written before the
    /// column existed or whose value could not be converted, when the field is present in <c>payload</c>.
    /// Parameters: <c>@batch</c>, <c>@period_seconds</c>, and <c>@path_{i}</c> = <see cref="JsonPath"/> of each key.
    /// </summary>
    public static string DeleteBatch(SqlServerTableLayout layout, RetentionPass pass)
    {
        var sql = new StringBuilder(
            $"DELETE TOP (@batch) FROM {layout.QualifiedTable} WHERE [timestamp_utc] < DATEADD(SECOND, -@period_seconds, SYSDATETIMEOFFSET())");
        for (var i = 0; i < pass.KeepWhenPresent.Count; i++)
        {
            var column = SqlServerTableLayout.QuoteIdentifier(layout.ColumnFor(pass.KeepWhenPresent[i]));
            sql.Append($" AND {column} IS NULL AND JSON_VALUE([payload], @path_{i}) IS NULL");
        }

        return sql.ToString();
    }

    /// <summary>JSON path selecting <paramref name="key"/> as one top-level member (keys contain dots).</summary>
    public static string JsonPath(string key)
        => "$.\"" + key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
