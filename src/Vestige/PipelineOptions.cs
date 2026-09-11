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
    /// Default: 500 ms.
    /// </summary>
    public TimeSpan BatchFlushInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Behavior when the channel buffer is full. Default: <see cref="BufferFullBehavior.Drop"/>.</summary>
    public BufferFullBehavior OnBufferFull { get; set; } = BufferFullBehavior.Drop;

    /// <summary>
    /// Time allowed for sinks to flush after the host's shutdown token has already been cancelled.
    /// Applied only on the forced-shutdown path, so buffered events still reach their destination.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan ShutdownFlushTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> if any value is outside its supported range.
    /// Called by the pipeline at construction so misconfiguration fails fast and legibly.
    /// </summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BufferCapacity, 1, nameof(BufferCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1, nameof(BatchSize));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BatchSize, BufferCapacity, nameof(BatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BatchFlushInterval, TimeSpan.Zero, nameof(BatchFlushInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(ShutdownFlushTimeout, TimeSpan.Zero, nameof(ShutdownFlushTimeout));

        if (!Enum.IsDefined(OnBufferFull))
            throw new ArgumentOutOfRangeException(nameof(OnBufferFull), OnBufferFull, "Unknown buffer-full behavior.");
    }
}
