using System.Data;
using Microsoft.Data.SqlClient;

namespace Vestige.Sinks.SqlServer;

/// <summary>Everything about the table shape, computed once at initialization.</summary>
internal sealed class SqlServerTableLayout
{
    internal static readonly string[] FixedInsertColumns =
    [
        "event_id", "timestamp_utc", "service_name", "service_version", "deployment_environment", "cloud_region",
        "trace_id", "span_id", "outcome", "duration_ms", "http_status_code", "http_method", "http_path",
        "error_type", "payload",
    ];

    private static readonly Type[] FixedClrTypes =
    [
        typeof(string), typeof(DateTimeOffset), typeof(string), typeof(string), typeof(string), typeof(string),
        typeof(string), typeof(string), typeof(string), typeof(double), typeof(int), typeof(string), typeof(string),
        typeof(string), typeof(string),
    ];

    private readonly DataTable _template;
    private readonly Dictionary<string, string> _columnsByKey;

    public SqlServerTableLayout(SqlServerSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Promoted = options.PromotedFields.ToArray();
        QualifiedTable = $"{QuoteIdentifier(options.Schema)}.{QuoteIdentifier(options.Table)}";
        QualifiedTableLiteral = Literal(QualifiedTable);
        IndexPrefix = $"{options.Schema}_{options.Table}".Replace(".", "_", StringComparison.Ordinal);
        RetentionLockName = SqlAppLock.Resource(QualifiedTable, "retention");
        InsertColumns = [.. FixedInsertColumns, .. Promoted.Select(f => f.Column)];
        _columnsByKey = Promoted.ToDictionary(f => f.Key, f => f.Column, StringComparer.Ordinal);

        var columns = string.Join(", ", FixedInsertColumns.Concat(Promoted.Select(f => QuoteIdentifier(f.Column))));
        var values = string.Join(", ", FixedInsertColumns.Select(c => "@" + c).Concat(Promoted.Select((_, i) => $"@promoted_{i}")));
        InsertSql = $"INSERT INTO {QualifiedTable} ({columns}) VALUES ({values})";

        _template = new DataTable();
        for (var i = 0; i < FixedInsertColumns.Length; i++)
            _template.Columns.Add(FixedInsertColumns[i], FixedClrTypes[i]);
        foreach (var field in Promoted)
            _template.Columns.Add(field.Column, field.Type.ClrType);
    }

    public IReadOnlyList<PromotedField> Promoted { get; }

    /// <summary><c>[schema].[table]</c>.</summary>
    public string QualifiedTable { get; }

    /// <summary><see cref="QualifiedTable"/> escaped for use inside <c>N'...'</c>.</summary>
    public string QualifiedTableLiteral { get; }

    public string IndexPrefix { get; }

    public string RetentionLockName { get; }

    /// <summary>Insertable columns: fixed first, then promoted in declared order.</summary>
    public IReadOnlyList<string> InsertColumns { get; }

    public string InsertSql { get; }

    public string IndexName(PromotedField field) => $"{IndexPrefix}_{field.Column}_idx";

    /// <summary>App-lock resource guarding the build of <paramref name="field"/>'s index on this table.</summary>
    public string IndexLockName(PromotedField field) => SqlAppLock.Resource(QualifiedTable, "index|" + field.Column);

    public string ColumnFor(string key) => _columnsByKey[key];

    public DataTable CreateDataTable() => _template.Clone();

    public void AddRow(DataTable table, SqlServerEventRow row)
    {
        var values = new object[InsertColumns.Count];
        values[0] = row.EventId;
        values[1] = row.TimestampUtc;
        values[2] = (object?)row.ServiceName ?? DBNull.Value;
        values[3] = (object?)row.ServiceVersion ?? DBNull.Value;
        values[4] = (object?)row.DeploymentEnvironment ?? DBNull.Value;
        values[5] = (object?)row.CloudRegion ?? DBNull.Value;
        values[6] = (object?)row.TraceId ?? DBNull.Value;
        values[7] = (object?)row.SpanId ?? DBNull.Value;
        values[8] = (object?)row.Outcome ?? DBNull.Value;
        values[9] = row.DurationMs;
        values[10] = (object?)row.HttpStatusCode ?? DBNull.Value;
        values[11] = (object?)row.HttpMethod ?? DBNull.Value;
        values[12] = (object?)row.HttpPath ?? DBNull.Value;
        values[13] = (object?)row.ErrorType ?? DBNull.Value;
        values[14] = row.PayloadJson;
        for (var i = 0; i < Promoted.Count; i++)
            values[FixedInsertColumns.Length + i] = PromotedValueAt(row, i) ?? DBNull.Value;

        table.Rows.Add(values);
    }

    public void AddPromotedParameters(SqlCommand command, SqlServerEventRow row)
    {
        for (var i = 0; i < Promoted.Count; i++)
        {
            var field = Promoted[i];
            var parameter = new SqlParameter($"@promoted_{i}", field.Type.SqlDbType)
            {
                Value = PromotedValueAt(row, i) ?? DBNull.Value,
            };
            if (field.Type.Kind == PromotedKind.String)
                parameter.Size = field.Type.Length;
            command.Parameters.Add(parameter);
        }
    }

    public static string QuoteIdentifier(string identifier)
        => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static object? PromotedValueAt(SqlServerEventRow row, int index)
        => index < row.PromotedValues.Length ? row.PromotedValues[index] : null;
}
