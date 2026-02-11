using System.Collections.Concurrent;

namespace Vestige;

/// <summary>
/// Thread-safe accumulator for wide event fields throughout a request lifecycle.
/// Emit once at completion via the pipeline.
/// </summary>
public sealed class WideEvent
{
    private readonly ConcurrentDictionary<string, object?> _properties = new(StringComparer.Ordinal);

    /// <summary>Unique identifier for this event.</summary>
#if NET9_0_OR_GREATER
    public Guid EventId { get; } = Guid.CreateVersion7();
#else
    public Guid EventId { get; } = Guid.NewGuid();
#endif

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

    // ── Scopes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Start a named scope. All fields set on the returned <see cref="ScopedWideEvent"/>
    /// are written as <c>{name}.{key}</c> on this event. Disposing the scope records
    /// <c>{name}.duration_ms</c> with the elapsed wall-clock time.
    /// </summary>
    /// <example>
    /// Disposable form (works with async):
    /// <code>
    /// using var payment = ev.Scope("payment.charge");
    /// payment.Set("provider", "stripe");
    /// await _gateway.ChargeAsync();
    /// // → payment.charge.provider = "stripe", payment.charge.duration_ms = …
    /// </code>
    /// </example>
    public ScopedWideEvent Scope(string name) => new(this, name);

    /// <summary>
    /// Execute <paramref name="action"/> inside a named scope and record its
    /// duration automatically when the action returns.
    /// </summary>
    /// <example>
    /// <code>
    /// ev.Scope("db.fetch", s => s.Set("table", "orders"));
    /// // → db.fetch.table = "orders", db.fetch.duration_ms = …
    /// </code>
    /// </example>
    public WideEvent Scope(string name, Action<ScopedWideEvent> action)
    {
        using var scope = new ScopedWideEvent(this, name);
        action(scope);
        return this;
    }

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

}
