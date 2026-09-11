using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vestige.Internal;

/// <summary>
/// Background channel consumer that serializes, samples, and dispatches wide events.
/// Implements both <see cref="IWideEventPipeline"/> and <see cref="IHostedService"/>.
/// </summary>
internal sealed class WideEventPipeline : IWideEventPipeline, IHostedService, IAsyncDisposable
{
    private readonly Channel<WideEvent> _channel;
    private readonly IWideEventSerializer _serializer;
    private readonly TailSampler _sampler;
    private readonly IWideEventSink[] _sinks;
    private readonly PipelineOptions _options;
    private readonly ILogger<WideEventPipeline> _logger;
    private readonly bool _blockWhenFull;

    private Task _consumerTask = Task.CompletedTask;
    private CancellationTokenSource? _cts;
    private long _droppedEvents;

    public WideEventPipeline(
        IWideEventSerializer serializer,
        IEnumerable<ISamplingStrategy> strategies,
        IEnumerable<IWideEventSink> sinks,
        IOptions<PipelineOptions> options,
        ILogger<WideEventPipeline> logger)
    {
        _serializer = serializer;
        _sampler = new TailSampler(strategies);
        _sinks = sinks.ToArray();
        _options = options.Value;
        _logger = logger;

        _options.Validate();
        _blockWhenFull = _options.OnBufferFull == BufferFullBehavior.Block;

        var channelOptions = new BoundedChannelOptions(_options.BufferCapacity)
        {
            FullMode = _options.OnBufferFull switch
            {
                BufferFullBehavior.Block => BoundedChannelFullMode.Wait,
                BufferFullBehavior.DropOldest => BoundedChannelFullMode.DropOldest,
                _ => BoundedChannelFullMode.DropWrite,
            },
            SingleReader = true,
            SingleWriter = false,
        };
        // The itemDropped callback is the only reliable drop signal: under DropWrite and DropOldest
        // the channel discards the item and still reports success from TryWrite.
        _channel = Channel.CreateBounded<WideEvent>(channelOptions, _ => RecordDrop());
    }

    /// <inheritdoc/>
    public long DroppedEventCount => Interlocked.Read(ref _droppedEvents);

    /// <inheritdoc/>
    public ValueTask EmitAsync(WideEvent ev, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ev);

        // Deliberately not `async`: the non-blocking path is fully synchronous and runs once per
        // request, so it must not pay for an async state machine.
        if (_blockWhenFull)
            return _channel.Writer.WriteAsync(ev, cancellationToken);

        // Drops are reported through the channel's itemDropped callback, not this return value:
        // in the dropping modes TryWrite reports success even when the item is discarded.
        _channel.Writer.TryWrite(ev);
        return ValueTask.CompletedTask;
    }

    private void RecordDrop()
    {
        var dropped = Interlocked.Increment(ref _droppedEvents);

        // Loud on the first loss, then rate-limited so an overloaded pipeline cannot
        // amplify itself through the logger.
        if (dropped == 1 || dropped % 1000 == 0)
        {
            _logger.LogWarning(
                "Wide event buffer full; dropped {DroppedCount} event(s) so far. " +
                "Raise PipelineOptions.BufferCapacity or reduce sink latency.",
                dropped);
        }
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sink {Sink} failed to initialize.", sink.GetType().Name);
            }
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _consumerTask = ConsumeAsync(_cts.Token);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.Complete();

        var gracefulDrain = true;
        try
        {
            // Drain buffered events while the host shutdown deadline still allows it.
            await _consumerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            gracefulDrain = false;

            if (_cts is not null)
                await _cts.CancelAsync().ConfigureAwait(false);

            try
            {
                await _consumerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Forced shutdown path.
            }
        }

        // On the forced path the caller's token is already cancelled. Flushing with it would fail
        // every sink at exactly the moment the flush matters most, so fall back to a bounded budget.
        using var flushCts = gracefulDrain
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();

        if (!gracefulDrain && _options.ShutdownFlushTimeout > TimeSpan.Zero)
            flushCts.CancelAfter(_options.ShutdownFlushTimeout);

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.FlushAsync(flushCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sink {Sink} failed to flush on shutdown.", sink.GetType().Name);
            }
        }

        if (DroppedEventCount > 0)
            _logger.LogWarning("Vestige dropped {DroppedCount} event(s) due to a full buffer.", DroppedEventCount);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        var batch = new List<WideEventData>(_options.BatchSize);
        var batchStartedAt = 0L;

        while (true)
        {
            if (batch.Count == 0)
            {
                // Never pass the shutdown token here: a cancelled wait would abandon events that
                // are already sitting in the channel.
                if (!await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
                    break;
            }

            while (batch.Count < _options.BatchSize && reader.TryRead(out var ev))
            {
                ProcessEvent(ev, batch);
                if (batch.Count == 1)
                    batchStartedAt = Stopwatch.GetTimestamp();
            }

            if (batch.Count == 0)
                continue;

            if (batch.Count >= _options.BatchSize)
            {
                await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var remaining = _options.BatchFlushInterval - Stopwatch.GetElapsedTime(batchStartedAt);
            if (remaining <= TimeSpan.Zero ||
                !await WaitForMoreAsync(remaining, cancellationToken).ConfigureAwait(false))
            {
                await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }

        if (batch.Count > 0)
            await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);

        void ProcessEvent(WideEvent ev, List<WideEventData> targetBatch)
        {
            try
            {
                if (_sampler.Evaluate(ev) == SamplingDecision.Drop)
                    return;

                targetBatch.Add(_serializer.Serialize(ev));
            }
            catch (Exception ex)
            {
                // Unconditional: a throw here during shutdown would fault the consumer and abandon
                // every event still buffered behind this one.
                _logger.LogError(ex, "Error processing wide event.");
            }
        }
    }

    /// <summary>
    /// Wait for more events, giving up after <paramref name="timeout"/>. Returns false when the
    /// batch window expired, the channel completed, or shutdown was requested.
    /// </summary>
    private async ValueTask<bool> WaitForMoreAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The CTS is disposed on every path, so neither the timer nor the cancellation
        // registration outlives the wait.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await _channel.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task FlushBatchAsync(List<WideEventData> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return;

        // Hand sinks their own array: `batch` is cleared and refilled immediately, so a sink that
        // held the reference across an await would observe it mutate underneath.
        var payload = batch.ToArray();
        batch.Clear();

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.EmitBatchAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sink {Sink} failed to emit batch.", sink.GetType().Name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sink in _sinks)
        {
            try { await sink.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
        _cts?.Dispose();
    }
}
