using System.Diagnostics;

namespace Vestige;

/// <summary>
/// A prefix-scoped view of a <see cref="WideEvent"/>.
/// All field operations are namespaced under the scope's prefix:
/// <c>Set("key", v)</c> writes <c>{prefix}.key = v</c> on the parent event.
/// On <see cref="Dispose"/>, records <c>{prefix}.duration_ms</c> with the
/// elapsed wall-clock time since the scope was created.
/// </summary>
/// <example>
/// Disposable form — works with async code:
/// <code>
/// using var payment = ev?.Scope("payment.charge");
/// payment?.Set("provider", "stripe");
/// await _gateway.ChargeAsync(cart);
/// // on dispose → payment.charge.provider = "stripe"
/// //            → payment.charge.duration_ms = 289.4
/// </code>
/// Action form — for synchronous lambdas:
/// <code>
/// ev?.Scope("db.fetch", s =>
/// {
///     s.Set("table", "orders");
///     Thread.Sleep(5); // measured
/// });
/// // → db.fetch.table = "orders"
/// // → db.fetch.duration_ms = 5.1
/// </code>
/// Nested scopes:
/// <code>
/// using var checkout = ev?.Scope("checkout");
/// checkout?.Scope("validation", s => s.Set("rules_run", 4));
/// // → checkout.validation.rules_run = 4
/// // → checkout.validation.duration_ms = …
/// // → checkout.duration_ms = …
/// </code>
/// </example>
public sealed class ScopedWideEvent : IDisposable
{
    private readonly WideEvent _event;
    private readonly string _prefix;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private int _disposed;

    internal ScopedWideEvent(WideEvent ev, string prefix)
    {
        _event  = ev;
        _prefix = prefix;
    }

    // ── Field operations (all namespaced under prefix) ────────────────────

    /// <summary>Set <c>{prefix}.{key}</c> on the parent event.</summary>
    public ScopedWideEvent Set(string key, object? value)
    {
        _event.Set($"{_prefix}.{key}", value);
        return this;
    }

    /// <summary>Get <c>{prefix}.{key}</c> from the parent event.</summary>
    public T? Get<T>(string key) => _event.Get<T>($"{_prefix}.{key}");

    /// <summary>Returns true if <c>{prefix}.{key}</c> exists.</summary>
    public bool Has(string key) => _event.Has($"{_prefix}.{key}");

    /// <summary>Remove <c>{prefix}.{key}</c> from the parent event.</summary>
    public bool Remove(string key) => _event.Remove($"{_prefix}.{key}");

    // ── Nested scopes ─────────────────────────────────────────────────────

    /// <summary>
    /// Start a nested scope whose prefix is <c>{this.prefix}.{name}</c>.
    /// Dispose the returned scope to record its duration.
    /// </summary>
    public ScopedWideEvent Scope(string name)
        => new(_event, $"{_prefix}.{name}");

    /// <summary>
    /// Execute <paramref name="action"/> inside a nested scope and record
    /// its duration automatically when the action returns.
    /// </summary>
    public ScopedWideEvent Scope(string name, Action<ScopedWideEvent> action)
    {
        using var child = new ScopedWideEvent(_event, $"{_prefix}.{name}");
        action(child);
        return this;
    }

    // ── Lifetime ──────────────────────────────────────────────────────────

    /// <summary>
    /// Stops the timer and writes <c>{prefix}.duration_ms</c> onto the
    /// parent event. Safe to call multiple times; only the first call records.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _sw.Stop();
            _event.Set($"{_prefix}.duration_ms", _sw.Elapsed.TotalMilliseconds);
        }
    }
}
