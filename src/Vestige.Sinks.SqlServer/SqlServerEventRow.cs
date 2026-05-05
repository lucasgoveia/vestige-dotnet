using System.Globalization;
using System.Text;

namespace Vestige.Sinks.SqlServer;

internal sealed class SqlServerEventRow
{
    public required string EventId { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    public string? ServiceName { get; init; }

    public string? ServiceVersion { get; init; }

    public string? DeploymentEnvironment { get; init; }

    public string? CloudRegion { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public string? Outcome { get; init; }

    public required double DurationMs { get; init; }

    public int? HttpStatusCode { get; init; }

    public string? HttpMethod { get; init; }

    public string? HttpPath { get; init; }

    public string? ErrorType { get; init; }

    public required string PayloadJson { get; init; }

    public static SqlServerEventRow Map(WideEventData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var eventId = GetRequiredString(data.Fields, "event_id");
        if (eventId.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(eventId), "Event id cannot exceed 256 characters.");

        return new SqlServerEventRow
        {
            EventId = eventId,
            TimestampUtc = GetRequiredTimestamp(data.Fields, "timestamp"),
            ServiceName = GetOptionalString(data.Fields, "service.name"),
            ServiceVersion = GetOptionalString(data.Fields, "service.version"),
            DeploymentEnvironment = GetOptionalString(data.Fields, "deployment.environment"),
            CloudRegion = GetOptionalString(data.Fields, "cloud.region"),
            TraceId = GetOptionalString(data.Fields, "trace_id"),
            SpanId = GetOptionalString(data.Fields, "span_id"),
            Outcome = GetOptionalString(data.Fields, "outcome"),
            DurationMs = GetRequiredDouble(data.Fields, "duration_ms"),
            HttpStatusCode = GetOptionalInt(data.Fields, "http.status_code"),
            HttpMethod = GetOptionalString(data.Fields, "http.method"),
            HttpPath = GetOptionalString(data.Fields, "http.path"),
            ErrorType = GetOptionalString(data.Fields, "error.type"),
            PayloadJson = Encoding.UTF8.GetString(data.JsonBytes),
        };
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> fields, string key)
        => GetOptionalString(fields, key)
           ?? throw new InvalidOperationException($"Field '{key}' is required.");

    private static string? GetOptionalString(IReadOnlyDictionary<string, object?> fields, string key)
        => fields.TryGetValue(key, out var value) ? value as string : null;

    private static int? GetOptionalInt(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int number => number,
            long number => checked((int)number),
            _ => throw new InvalidOperationException($"Field '{key}' must be an integer."),
        };
    }

    private static double GetRequiredDouble(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || value is null)
            throw new InvalidOperationException($"Field '{key}' is required.");

        return value switch
        {
            double number => number,
            float number => number,
            int number => number,
            long number => number,
            _ => throw new InvalidOperationException($"Field '{key}' must be numeric."),
        };
    }

    private static DateTimeOffset GetRequiredTimestamp(IReadOnlyDictionary<string, object?> fields, string key)
    {
        var raw = GetRequiredString(fields, key);
        return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
