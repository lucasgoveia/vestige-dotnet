namespace Vestige.Testing;

/// <summary>Fluent assertion context for <see cref="WideEvent"/>.</summary>
public sealed class WideEventAssertions
{
    private readonly WideEvent _ev;

    internal WideEventAssertions(WideEvent ev) => _ev = ev;

    /// <summary>Assert that the event has a field with the expected value.</summary>
    public WideEventAssertions HaveField(string key, object? expectedValue)
    {
        if (!_ev.Has(key))
            throw new AssertionException($"Expected WideEvent to have field '{key}', but it was not found.");

        var actual = _ev.Get<object>(key);
        if (!Equals(actual, expectedValue))
            throw new AssertionException($"Expected WideEvent field '{key}' to be '{expectedValue}', but was '{actual}'.");

        return this;
    }

    /// <summary>Assert that the event has a field (regardless of value).</summary>
    public WideEventAssertions HaveField(string key)
    {
        if (!_ev.Has(key))
            throw new AssertionException($"Expected WideEvent to have field '{key}', but it was not found.");
        return this;
    }

    /// <summary>Assert that a named timer recorded at least <paramref name="minMs"/> milliseconds.</summary>
    public WideEventAssertions HaveTimingGreaterThan(string operationName, long minMs)
    {
        if (!_ev.Timings.TryGetValue(operationName, out var ms))
            throw new AssertionException($"Expected WideEvent to have timing '{operationName}', but it was not found.");

        if (ms < minMs)
            throw new AssertionException($"Expected timing '{operationName}' to be >= {minMs}ms, but was {ms}ms.");

        return this;
    }

    /// <summary>Assert that <see cref="WideEvent.Outcome"/> equals <paramref name="expected"/>.</summary>
    public WideEventAssertions HaveOutcome(string expected)
    {
        if (_ev.Outcome != expected)
            throw new AssertionException($"Expected WideEvent outcome '{expected}', but was '{_ev.Outcome}'.");
        return this;
    }
}

/// <summary>Extension entry-point for <see cref="WideEventAssertions"/>.</summary>
public static class WideEventAssertionExtensions
{
    /// <summary>Begin fluent assertions on a <see cref="WideEvent"/>.</summary>
    public static WideEventAssertions Should(this WideEvent ev) => new(ev);
}

/// <summary>Thrown when a <see cref="WideEventAssertions"/> check fails.</summary>
public sealed class AssertionException(string message) : Exception(message);
