using System.Diagnostics;

namespace Vestige;

/// <summary>
/// Thread-safe accumulator for wide event fields throughout a request lifecycle.
/// Emit once at completion via the pipeline.
/// </summary>
/// <remarks>
/// Every member — the property bag, the timers, and the fixed scalar fields — is guarded by a
/// single internal monitor, so concurrent reads and writes from request threads, enrichers, and
/// the pipeline consumer are safe. Field additions are bounded by <see cref="WideEventLimits"/>.
/// </remarks>
public sealed class WideEvent
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _properties = new(StringComparer.Ordinal);

    // Allocated on first use — the overwhelming majority of events record no sub-timers.
    private Dictionary<string, double>? _timings;

    private string? _eventId;
    private string? _serviceName;
    private string? _traceId;
    private string? _spanId;
    private string? _outcome;
    private double _durationMs;
    private int? _statusCode;
    private int _droppedFieldCount;

    /// <summary>Creates an event using <see cref="WideEventLimits.Default"/>.</summary>
    public WideEvent() : this(WideEventLimits.Default)
    {
    }

    /// <summary>Creates an event bounded by <paramref name="limits"/>.</summary>
    public WideEvent(WideEventLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Limits = limits;
    }

    /// <summary>The caps applied as fields are added to this event.</summary>
    public WideEventLimits Limits { get; }

    /// <summary>
    /// Unique identifier for this event. Generated lazily on first access, so events discarded by
    /// tail sampling never pay for the <see cref="Guid"/>.
    /// </summary>
    public string EventId
    {
        get
        {
            lock (_gate)
                return _eventId ??= Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>UTC timestamp when this event was created.</summary>
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;

    /// <summary>The logical name of the service emitting this event.</summary>
    public string? ServiceName
    {
        get { lock (_gate) return _serviceName; }
        set { lock (_gate) _serviceName = value; }
    }

    /// <summary>Distributed trace identifier (W3C TraceId).</summary>
    public string? TraceId
    {
        get { lock (_gate) return _traceId; }
        set { lock (_gate) _traceId = value; }
    }

    /// <summary>Distributed span identifier (W3C SpanId).</summary>
    public string? SpanId
    {
        get { lock (_gate) return _spanId; }
        set { lock (_gate) _spanId = value; }
    }

    /// <summary>Total request duration in milliseconds. Set by middleware before emit.</summary>
    public double DurationMs
    {
        get { lock (_gate) return _durationMs; }
        set { lock (_gate) _durationMs = value; }
    }

    /// <summary>Outcome label: "success", "error", "cancelled", etc.</summary>
    public string? Outcome
    {
        get { lock (_gate) return _outcome; }
        set { lock (_gate) _outcome = value; }
    }

    /// <summary>HTTP status code, if applicable.</summary>
    public int? StatusCode
    {
        get { lock (_gate) return _statusCode; }
        set { lock (_gate) _statusCode = value; }
    }

    /// <summary>
    /// Number of fields discarded because <see cref="WideEventLimits.MaxFieldCount"/> was reached.
    /// Surfaced on the serialized event as <c>vestige.dropped_fields</c> when non-zero.
    /// </summary>
    public int DroppedFieldCount
    {
        get { lock (_gate) return _droppedFieldCount; }
    }

    /// <summary>
    /// Set <see cref="Outcome"/> only if it has not already been set, atomically.
    /// Returns true if this call assigned the value.
    /// </summary>
    public bool TrySetOutcome(string outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        lock (_gate)
        {
            if (_outcome is not null)
                return false;
            _outcome = outcome;
            return true;
        }
    }

    // ── Dynamic property bag ──────────────────────────────────────────────────

    /// <summary>
    /// Set a named field value. Keys and string values longer than the configured
    /// <see cref="Limits"/> are truncated; fields past <see cref="WideEventLimits.MaxFieldCount"/>
    /// are discarded and counted in <see cref="DroppedFieldCount"/>.
    /// </summary>
    public WideEvent Set(string key, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
            SetCore(key, value);
        return this;
    }

    /// <summary>Set multiple fields from a dictionary. Subject to the same limits as <see cref="Set"/>.</summary>
    public WideEvent SetMany(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        lock (_gate)
        {
            foreach (var (key, value) in fields)
                SetCore(key, value);
        }
        return this;
    }

    /// <summary>Get a typed field value, or default if not present.</summary>
    public T? Get<T>(string key)
    {
        lock (_gate)
        {
            if (_properties.TryGetValue(key, out var value) && value is T typed)
                return typed;
            return default;
        }
    }

    /// <summary>Returns true if the field exists (including null values).</summary>
    public bool Has(string key)
    {
        lock (_gate)
            return _properties.ContainsKey(key);
    }

    /// <summary>Remove a field. Returns true if it existed.</summary>
    public bool Remove(string key)
    {
        lock (_gate)
            return _properties.Remove(key);
    }

    /// <summary>Point-in-time snapshot of all dynamic properties.</summary>
    public IReadOnlyDictionary<string, object?> Properties
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, object?>(_properties, StringComparer.Ordinal);
        }
    }

    // ── Sub-timers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Start a named timer. Dispose the returned handle to record elapsed milliseconds
    /// under the key <c>timer.{operationName}</c>. Repeated calls with the same name accumulate.
    /// </summary>
    public IDisposable Time(string operationName)
    {
        ArgumentNullException.ThrowIfNull(operationName);
        return new TimerHandle(this, Limits.TruncateKey(operationName), Stopwatch.GetTimestamp());
    }

    /// <summary>Point-in-time snapshot of all recorded timer durations, in milliseconds.</summary>
    public IReadOnlyDictionary<string, double> Timings
    {
        get
        {
            lock (_gate)
            {
                return _timings is null
                    ? EmptyTimings
                    : new Dictionary<string, double>(_timings, StringComparer.Ordinal);
            }
        }
    }

    private static readonly Dictionary<string, double> EmptyTimings = new(StringComparer.Ordinal);

    // ── Error capture ─────────────────────────────────────────────────────────

    /// <summary>
    /// Capture structured fields from an exception:
    /// <c>error.type</c>, <c>error.message</c>, <c>error.stack_trace</c>.
    /// Also sets <see cref="Outcome"/> to "error" if not already set.
    /// The stack trace is truncated to <see cref="WideEventLimits.MaxStackTraceLength"/>.
    /// </summary>
    public WideEvent CaptureException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        lock (_gate)
        {
            SetCore("error.type", ex.GetType().FullName);
            SetCore("error.message", ex.Message);
            SetCore("error.stack_trace", Limits.TruncateStackTrace(ex.StackTrace));
            _outcome ??= "error";
        }
        return this;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Copy every field — fixed, dynamic, and timer — into <paramref name="target"/> under a single
    /// lock, so serializers observe a consistent snapshot without an intermediate allocation.
    /// </summary>
    internal void CopyFieldsTo(Dictionary<string, object?> target)
    {
        lock (_gate)
        {
            target["event_id"] = _eventId ??= Guid.NewGuid().ToString("N");
            target["timestamp"] = Timestamp;
            if (_serviceName is not null) target["service.name"] = _serviceName;
            if (_traceId is not null) target["trace_id"] = _traceId;
            if (_spanId is not null) target["span_id"] = _spanId;
            target["duration_ms"] = _durationMs;
            if (_outcome is not null) target["outcome"] = _outcome;
            if (_statusCode.HasValue) target["http.status_code"] = _statusCode.Value;

            foreach (var (key, value) in _properties)
                target[key] = value;

            if (_timings is not null)
            {
                foreach (var (key, ms) in _timings)
                    target[string.Concat("timer.", key)] = ms;
            }

            if (_droppedFieldCount > 0)
                target["vestige.dropped_fields"] = _droppedFieldCount;
        }
    }

    /// <summary>Must be called while holding <see cref="_gate"/>.</summary>
    private void SetCore(string key, object? value)
    {
        var boundedKey = Limits.TruncateKey(key);

        // Overwriting an existing key never grows the bag, so it is always allowed.
        if (_properties.Count >= Limits.MaxFieldCount && !_properties.ContainsKey(boundedKey))
        {
            _droppedFieldCount++;
            return;
        }

        _properties[boundedKey] = Limits.TruncateValue(value);
    }

    private void RecordTiming(string key, double elapsedMs)
    {
        lock (_gate)
        {
            _timings ??= new Dictionary<string, double>(StringComparer.Ordinal);
            _timings[key] = _timings.TryGetValue(key, out var prev) ? prev + elapsedMs : elapsedMs;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private sealed class TimerHandle(WideEvent owner, string key, long startTimestamp) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.RecordTiming(key, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }
}
