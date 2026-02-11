using System.Collections.Concurrent;
using System.Diagnostics;

namespace Vestige;

/// <summary>
/// Thread-safe accumulator for wide event fields throughout a request lifecycle.
/// Emit once at completion via the pipeline.
/// </summary>
public sealed class WideEvent
{
    private readonly ConcurrentDictionary<string, object?> _properties = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _timings = new(StringComparer.Ordinal);

    /// <summary>Unique identifier for this event.</summary>
    public string EventId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>UTC timestamp when this event was created.</summary>
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;

    /// <summary>The logical name of the service emitting this event.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Distributed trace identifier (W3C TraceId).</summary>
    public string? TraceId { get; set; }

    /// <summary>Distributed span identifier (W3C SpanId).</summary>
    public string? SpanId { get; set; }

    /// <summary>Total request duration in milliseconds. Set by middleware before emit.</summary>
    public double DurationMs { get; set; }

    /// <summary>Outcome label: "success", "error", "cancelled", etc.</summary>
    public string? Outcome { get; set; }

    /// <summary>HTTP status code, if applicable.</summary>
    public int? StatusCode { get; set; }

    // ── Dynamic property bag ──────────────────────────────────────────────────

    /// <summary>Set a named field value.</summary>
    public WideEvent Set(string key, object? value)
    {
        _properties[key] = value;
        return this;
    }

    /// <summary>Set multiple fields from a dictionary.</summary>
    public WideEvent SetMany(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        foreach (var (key, value) in fields)
            _properties[key] = value;
        return this;
    }

    /// <summary>Get a typed field value, or default if not present.</summary>
    public T? Get<T>(string key)
    {
        if (_properties.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return default;
    }

    /// <summary>Returns true if the field exists (including null values).</summary>
    public bool Has(string key) => _properties.ContainsKey(key);

    /// <summary>Remove a field. Returns true if it existed.</summary>
    public bool Remove(string key) => _properties.TryRemove(key, out _);

    /// <summary>Snapshot of all dynamic properties.</summary>
    public IReadOnlyDictionary<string, object?> Properties => _properties;

    // ── Sub-timers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Start a named timer. Dispose the returned handle to record elapsed milliseconds
    /// under the key <c>timer.{operationName}</c>.
    /// </summary>
    public IDisposable Time(string operationName)
    {
        var sw = Stopwatch.StartNew();
        return new TimerHandle(() =>
        {
            sw.Stop();
            _timings.AddOrUpdate(operationName, sw.ElapsedMilliseconds, (_, prev) => prev + sw.ElapsedMilliseconds);
        });
    }

    /// <summary>Snapshot of all recorded timer durations (ms).</summary>
    public IReadOnlyDictionary<string, long> Timings => _timings;

    // ── Error capture ─────────────────────────────────────────────────────────

    /// <summary>
    /// Capture structured fields from an exception:
    /// <c>error.type</c>, <c>error.message</c>, <c>error.stack_trace</c>.
    /// Also sets <see cref="Outcome"/> to "error" if not already set.
    /// </summary>
    public WideEvent CaptureException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        _properties["error.type"] = ex.GetType().FullName;
        _properties["error.message"] = ex.Message;
        _properties["error.stack_trace"] = ex.StackTrace;
        Outcome ??= "error";
        return this;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private sealed class TimerHandle(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
        }
    }
}
