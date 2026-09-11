using System.Buffers;
using System.Text.Json;

namespace Vestige.Internal;

/// <summary>Serializes a <see cref="WideEvent"/> to UTF-8 JSON using System.Text.Json.</summary>
/// <remarks>
/// Common scalar types are written straight to the <see cref="Utf8JsonWriter"/>, bypassing
/// reflection entirely. Anything else falls back to <see cref="JsonSerializer"/>; if that fails
/// — a cyclic graph, or missing metadata under trimming — the value degrades to its string form
/// rather than losing the whole event.
/// </remarks>
internal sealed class SystemTextJsonSerializer : IWideEventSerializer
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = false,
    };

    // Reused per thread: the pipeline consumer serializes in a tight loop, and this keeps the
    // buffer and writer out of the per-event allocation path.
    [ThreadStatic] private static ArrayBufferWriter<byte>? t_buffer;
    [ThreadStatic] private static Utf8JsonWriter? t_writer;

    /// <inheritdoc/>
    public WideEventData Serialize(WideEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        ev.CopyFieldsTo(fields);

        return new WideEventData
        {
            Fields = fields,
            JsonBytes = WriteJson(fields),
            OriginalEvent = ev,
        };
    }

    private static byte[] WriteJson(Dictionary<string, object?> fields)
    {
        var buffer = t_buffer ??= new ArrayBufferWriter<byte>(initialCapacity: 4096);
        var writer = t_writer ??= new Utf8JsonWriter(buffer);

        buffer.Clear();
        writer.Reset(buffer);

        writer.WriteStartObject();
        foreach (var (key, value) in fields)
            WriteField(writer, key, value);
        writer.WriteEndObject();
        writer.Flush();

        var bytes = buffer.WrittenSpan.ToArray();

        // Don't let one oversized event pin a large buffer for the lifetime of the thread.
        if (buffer.Capacity > 64 * 1024)
        {
            t_buffer = null;
            t_writer = null;
            writer.Dispose();
        }

        return bytes;
    }

    private static void WriteField(Utf8JsonWriter writer, string key, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNull(key); return;
            case string s: writer.WriteString(key, s); return;
            case bool b: writer.WriteBoolean(key, b); return;
            case int i: writer.WriteNumber(key, i); return;
            case long l: writer.WriteNumber(key, l); return;
            case double d: WriteDouble(writer, key, d); return;
            case float f: WriteDouble(writer, key, f); return;
            case decimal m: writer.WriteNumber(key, m); return;
            case DateTimeOffset dto: writer.WriteString(key, dto); return;
            case DateTime dt: writer.WriteString(key, dt); return;
            case Guid g: writer.WriteString(key, g); return;
            case TimeSpan ts: writer.WriteString(key, ts.ToString("c")); return;
            case uint ui: writer.WriteNumber(key, ui); return;
            case ulong ul: writer.WriteNumber(key, ul); return;
            case short sh: writer.WriteNumber(key, sh); return;
            case ushort ush: writer.WriteNumber(key, ush); return;
            case byte by: writer.WriteNumber(key, by); return;
            case sbyte sby: writer.WriteNumber(key, sby); return;
            case char c: writer.WriteString(key, c.ToString()); return;
            case Enum e: writer.WriteString(key, e.ToString()); return;
        }

        // Serialize into a scratch buffer first: a failure part-way through would otherwise leave
        // the shared writer in a corrupt state and take the whole event down with it.
        byte[]? raw = null;
        try
        {
            raw = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), s_options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Fall through to the string form below.
        }

        writer.WritePropertyName(key);
        if (raw is not null)
            writer.WriteRawValue(raw, skipInputValidation: true);
        else
            writer.WriteStringValue(SafeToString(value));
    }

    // JSON has no representation for NaN or the infinities; emit them as strings instead of
    // throwing and losing the event.
    private static void WriteDouble(Utf8JsonWriter writer, string key, double value)
    {
        if (double.IsFinite(value))
            writer.WriteNumber(key, value);
        else
            writer.WriteString(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string SafeToString(object value)
    {
        try { return value.ToString() ?? value.GetType().FullName ?? "?"; }
        catch { return value.GetType().FullName ?? "?"; }
    }
}
