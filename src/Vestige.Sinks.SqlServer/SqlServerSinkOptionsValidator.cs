namespace Vestige.Sinks.SqlServer;

internal static class SqlServerSinkOptionsValidator
{
    internal static readonly IReadOnlySet<string> ReservedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "id", "event_id", "timestamp_utc", "service_name", "service_version", "deployment_environment",
        "cloud_region", "trace_id", "span_id", "outcome", "duration_ms", "http_status_code",
        "http_method", "http_path", "error_type", "payload",
    };

    public static void Validate(SqlServerSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in options.PromotedFields)
        {
            if (!keys.Add(field.Key))
                throw new InvalidOperationException($"Field '{field.Key}' is promoted more than once.");

            ValidateColumnName(field);

            if (ReservedColumns.Contains(field.Column))
                throw new InvalidOperationException(
                    $"Promoted field '{field.Key}' maps to column [{field.Column}], which is reserved by the sink. Pass column: to choose another name.");

            if (!columns.Add(field.Column))
                throw new InvalidOperationException(
                    $"Column [{field.Column}] is used by more than one promoted field.");
        }

        options.Retention.Validate();

        foreach (var rule in options.Retention.Rules)
        {
            if (!keys.Contains(rule.Key))
                throw new InvalidOperationException($"retention rule on '{rule.Key}' requires a promoted field");
        }
    }

    private static void ValidateColumnName(PromotedField field)
    {
        var column = field.Column;
        if (column.Length is 0 or > 128)
            throw new InvalidOperationException(
                $"Promoted field '{field.Key}' column name must be 1 to 128 characters.");

        var first = column[0];
        var validFirst = char.IsAsciiLetter(first) || first == '_';
        var validRest = column.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        if (!validFirst || !validRest)
            throw new InvalidOperationException(
                $"Promoted field '{field.Key}' column [{column}] must start with a letter or underscore and contain only letters, digits and underscores.");
    }
}
