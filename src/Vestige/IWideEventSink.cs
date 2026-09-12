namespace Vestige;

/// <summary>Receives serialized wide events and delivers them to an output destination.</summary>
/// <remarks>
/// Sinks are singletons invoked from the pipeline's single consumer loop. A slow sink applies
/// backpressure to the whole pipeline, so do the work asynchronously or buffer internally.
/// Exceptions thrown from any member are caught and logged; they never reach the request.
/// </remarks>
public interface IWideEventSink : IAsyncDisposable
{
    /// <summary>Called once at startup; perform connection/resource setup here.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Emit a single event.</summary>
    Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default);

    /// <summary>Emit a batch of events.</summary>
    /// <param name="batch">
    /// The events to emit. Valid only for the duration of the call — do not retain the list
    /// itself beyond the returned task. Individual <see cref="WideEventData"/> instances are
    /// immutable and may be kept.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the host is shutting down.</param>
    Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default);

    /// <summary>Flush any buffered data. Called before graceful shutdown.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
