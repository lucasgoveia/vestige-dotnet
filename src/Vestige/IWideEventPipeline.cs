namespace Vestige;

/// <summary>
/// Internal pipeline that accepts completed wide events, serializes them,
/// applies tail sampling, and dispatches to registered sinks.
/// </summary>
public interface IWideEventPipeline
{
    /// <summary>Submit a completed event for processing.</summary>
    ValueTask EmitAsync(WideEvent ev, CancellationToken cancellationToken = default);

    /// <summary>
    /// Number of events lost because the buffer was full — both events rejected under
    /// <see cref="BufferFullBehavior.Drop"/> and older events evicted under
    /// <see cref="BufferFullBehavior.DropOldest"/>. Monotonic for the lifetime of the process.
    /// </summary>
    /// <remarks>
    /// Always zero under <see cref="BufferFullBehavior.Block"/>, which applies backpressure
    /// instead of discarding. Alert on any sustained increase: it means events are being lost.
    /// </remarks>
    long DroppedEventCount { get; }
}
