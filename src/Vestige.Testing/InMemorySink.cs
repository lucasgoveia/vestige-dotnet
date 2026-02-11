using System.Collections.Concurrent;

namespace Vestige.Testing;

/// <summary>
/// In-memory sink for testing. Captures all emitted events in a <see cref="ConcurrentBag{T}"/>.
/// </summary>
public sealed class InMemorySink : IWideEventSink
{
    private readonly ConcurrentBag<WideEventData> _events = new();

    /// <summary>Snapshot of all captured events in emission order (approximate).</summary>
    public IReadOnlyList<WideEventData> Events => _events.ToArray();

    /// <summary>Clear all captured events.</summary>
    public void Clear()
    {
        while (_events.TryTake(out _)) { }
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default)
    {
        _events.Add(data);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default)
    {
        foreach (var data in batch)
            _events.Add(data);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
