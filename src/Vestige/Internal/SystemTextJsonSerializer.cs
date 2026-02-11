using System.Text.Json;

namespace Vestige.Internal;

/// <summary>Serializes a <see cref="WideEvent"/> to UTF-8 JSON using System.Text.Json.</summary>
internal sealed class SystemTextJsonSerializer : IWideEventSerializer
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = false,
    };

    /// <inheritdoc/>
    public WideEventData Serialize(WideEvent ev)
    {
        var fields = BuildFields(ev);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fields, s_options);
        return new WideEventData
        {
            Fields = fields,
            JsonBytes = bytes,
            OriginalEvent = ev,
        };
    }

    private static Dictionary<string, object?> BuildFields(WideEvent ev)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Fixed fields (nulls omitted)
        dict["event_id"] = ev.EventId;
        dict["timestamp"] = ev.Timestamp.ToString("O");
        if (ev.ServiceName is not null) dict["service.name"] = ev.ServiceName;
        if (ev.TraceId is not null) dict["trace_id"] = ev.TraceId;
        if (ev.SpanId is not null) dict["span_id"] = ev.SpanId;
        dict["duration_ms"] = ev.DurationMs;
        if (ev.Outcome is not null) dict["outcome"] = ev.Outcome;
        if (ev.StatusCode.HasValue) dict["http.status_code"] = ev.StatusCode.Value;

        // Dynamic properties
        foreach (var (key, value) in ev.Properties)
            dict[key] = value;

        // Timings — prefixed with "timer."
        foreach (var (key, ms) in ev.Timings)
            dict[$"timer.{key}"] = ms;

        return dict;
    }
}
