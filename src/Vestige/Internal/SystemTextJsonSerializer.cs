using System.Buffers;
using System.Text.Json;

namespace Vestige.Internal;

/// <summary>Serializes a <see cref="WideEvent"/> to UTF-8 JSON using System.Text.Json.</summary>
internal sealed class SystemTextJsonSerializer : IWideEventSerializer
{
    private static readonly JsonSerializerOptions s_fallbackOptions = new()
    {
        WriteIndented = false,
    };

    /// <inheritdoc/>
    public WideEventData Serialize(WideEvent ev)
    {
        var fields = BuildFieldsAndJson(ev, out var bytes);
        return new WideEventData
        {
            Fields = fields,
            JsonBytes = bytes,
            OriginalEvent = ev,
        };
    }

    private static Dictionary<string, object?> BuildFieldsAndJson(WideEvent ev, out byte[] jsonBytes)
    {
        var capacity = 6 + ev.Properties.Count;
        var dict = new Dictionary<string, object?>(capacity, StringComparer.Ordinal);

        var buffer = new ArrayBufferWriter<byte>(Math.Max(256, capacity * 32));
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();

        AddField(dict, writer, "event_id", ev.EventId);
        var timestamp = ev.Timestamp.ToString("O");
        AddField(dict, writer, "timestamp", timestamp);
        if (ev.ServiceName is not null) AddField(dict, writer, "service.name", ev.ServiceName);
        if (ev.TraceId is not null) AddField(dict, writer, "trace_id", ev.TraceId);
        if (ev.SpanId is not null) AddField(dict, writer, "span_id", ev.SpanId);
        AddField(dict, writer, "duration_ms", ev.DurationMs);
        if (ev.Outcome is not null) AddField(dict, writer, "outcome", ev.Outcome);
        if (ev.StatusCode.HasValue) AddField(dict, writer, "http.status_code", ev.StatusCode.Value);

        foreach (var (key, value) in ev.Properties)
            AddField(dict, writer, key, value);

        writer.WriteEndObject();
        writer.Flush();

        jsonBytes = buffer.WrittenSpan.ToArray();
        return dict;
    }

    private static void AddField(
        Dictionary<string, object?> dict,
        Utf8JsonWriter writer,
        string key,
        object? value)
    {
        dict[key] = value;
        WriteValue(writer, key, value);
    }

    private static void WriteValue(Utf8JsonWriter writer, string key, object? value)
    {
        if (value is null)
        {
            writer.WriteNull(key);
            return;
        }

        switch (value)
        {
            case string text:
                writer.WriteString(key, text);
                return;
            case bool flag:
                writer.WriteBoolean(key, flag);
                return;
            case int i32:
                writer.WriteNumber(key, i32);
                return;
            case long i64:
                writer.WriteNumber(key, i64);
                return;
            case short i16:
                writer.WriteNumber(key, i16);
                return;
            case byte u8:
                writer.WriteNumber(key, u8);
                return;
            case uint u32:
                writer.WriteNumber(key, u32);
                return;
            case ulong u64:
                writer.WriteNumber(key, u64);
                return;
            case float f32:
                writer.WriteNumber(key, f32);
                return;
            case double f64:
                writer.WriteNumber(key, f64);
                return;
            case decimal dec:
                writer.WriteNumber(key, dec);
                return;
            case Guid guid:
                writer.WriteString(key, guid);
                return;
            case DateTime dateTime:
                writer.WriteString(key, dateTime);
                return;
            case DateTimeOffset dateTimeOffset:
                writer.WriteString(key, dateTimeOffset);
                return;
            case JsonElement element:
                writer.WritePropertyName(key);
                element.WriteTo(writer);
                return;
            default:
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, value, value.GetType(), s_fallbackOptions);
                return;
        }
    }
}
