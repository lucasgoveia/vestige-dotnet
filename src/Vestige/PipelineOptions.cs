namespace Vestige;

/// <summary>Configuration for the internal event pipeline channel and dispatch.</summary>
public sealed class PipelineOptions
{
    /// <summary>Maximum number of events buffered in the channel. Default: 10,000.</summary>
    public int BufferCapacity { get; set; } = 10_000;

    /// <summary>Maximum events dispatched per sink batch call. Default: 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum time to wait before flushing a partial batch.
    /// Default: 500 ms. (Phase 1: timer not implemented; flushes on batch-full or shutdown only.)
    /// </summary>
    public TimeSpan BatchFlushInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Behavior when the channel buffer is full. Default: <see cref="BufferFullBehavior.Drop"/>.</summary>
    public BufferFullBehavior OnBufferFull { get; set; } = BufferFullBehavior.Drop;
}
