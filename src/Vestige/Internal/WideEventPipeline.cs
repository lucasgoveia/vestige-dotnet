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
    private readonly IReadOnlyList<IWideEventSink> _sinks;
    private readonly PipelineOptions _options;
    private readonly ILogger<WideEventPipeline> _logger;
    private Task _consumerTask = Task.CompletedTask;
    private CancellationTokenSource _cts = new();

    public WideEventPipeline(
        IWideEventSerializer serializer,
        IEnumerable<ISamplingStrategy> strategies,
        IEnumerable<IWideEventSink> sinks,
        IOptions<PipelineOptions> options,
        ILogger<WideEventPipeline> logger)
    {
        _serializer = serializer;
        _sampler = new TailSampler(strategies);
        _sinks = sinks.ToList();
        _options = options.Value;
        _logger = logger;

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
        _channel = Channel.CreateBounded<WideEvent>(channelOptions);
    }

    /// <inheritdoc/>
    public async ValueTask EmitAsync(WideEvent ev, CancellationToken cancellationToken = default)
    {
        if (_options.OnBufferFull == BufferFullBehavior.Block)
        {
            await _channel.Writer.WriteAsync(ev, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _channel.Writer.TryWrite(ev);
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

        _cts = new CancellationTokenSource();
        _consumerTask = ConsumeAsync(_cts.Token);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.Complete();

        try
        {
            // Drain buffered events while the host shutdown deadline still allows it.
            await _consumerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sink {Sink} failed to flush on shutdown.", sink.GetType().Name);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var batch = new List<WideEventData>(_options.BatchSize);
        DateTimeOffset? batchStartedAt = null;

        while (await _channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var ev))
            {
                ProcessEvent(ev, batch, ref batchStartedAt, cancellationToken);

                if (batch.Count >= _options.BatchSize)
                    await FlushBatchAsync(batch).ConfigureAwait(false);
            }

            while (batch.Count > 0 && batch.Count < _options.BatchSize)
            {
                var startedAt = batchStartedAt ?? DateTimeOffset.UtcNow;
                var remaining = _options.BatchFlushInterval - (DateTimeOffset.UtcNow - startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    await FlushBatchAsync(batch).ConfigureAwait(false);
                    break;
                }

                try
                {
                    var waitToReadTask = _channel.Reader.WaitToReadAsync(CancellationToken.None).AsTask();
                    var delayTask = Task.Delay(remaining, cancellationToken);
                    var completedTask = await Task.WhenAny(waitToReadTask, delayTask).ConfigureAwait(false);

                    if (completedTask == delayTask)
                    {
                        await FlushBatchAsync(batch).ConfigureAwait(false);
                        break;
                    }

                    if (!await waitToReadTask.ConfigureAwait(false))
                        break;

                    while (batch.Count < _options.BatchSize && _channel.Reader.TryRead(out var ev))
                    {
                        ProcessEvent(ev, batch, ref batchStartedAt, cancellationToken);

                        if (batch.Count >= _options.BatchSize)
                        {
                            await FlushBatchAsync(batch).ConfigureAwait(false);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await FlushBatchAsync(batch).ConfigureAwait(false);
                    break;
                }
            }
        }

        if (batch.Count > 0)
            await FlushBatchAsync(batch).ConfigureAwait(false);

        return;

        void ProcessEvent(
            WideEvent ev,
            List<WideEventData> targetBatch,
            ref DateTimeOffset? startedAt,
            CancellationToken processingCancellationToken)
        {
            try
            {
                if (_sampler.Evaluate(ev) == SamplingDecision.Drop)
                    return;

                var data = _serializer.Serialize(ev);
                targetBatch.Add(data);
                startedAt ??= DateTimeOffset.UtcNow;
            }
            catch (Exception ex) when (!processingCancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error processing wide event.");
            }
        }

        async Task FlushBatchAsync(List<WideEventData> targetBatch)
        {
            await DispatchBatchAsync(targetBatch, cancellationToken).ConfigureAwait(false);
            targetBatch.Clear();
            batchStartedAt = null;
        }
    }

    private async Task DispatchBatchAsync(List<WideEventData> batch, CancellationToken cancellationToken)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.EmitBatchAsync(batch, cancellationToken).ConfigureAwait(false);
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
        _cts.Dispose();
    }
}
