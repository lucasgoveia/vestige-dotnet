namespace Vestige;

/// <summary>Receives serialized wide events and delivers them to an output destination.</summary>
public interface IWideEventSink : IAsyncDisposable
{
    /// <summary>Called once at startup; perform connection/resource setup here.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Emit a single event.</summary>
    Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default);

    /// <summary>Emit a batch of events. Default implementation loops over <see cref="EmitAsync"/>.</summary>
    Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default);

    /// <summary>Flush any buffered data. Called before graceful shutdown.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
