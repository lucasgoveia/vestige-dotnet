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
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            // Drain the channel before shutting down
            await _consumerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected */ }

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

        await foreach (var ev in _channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                if (_sampler.Evaluate(ev) == SamplingDecision.Drop)
                    continue;

                var data = _serializer.Serialize(ev);
                batch.Add(data);

                if (batch.Count >= _options.BatchSize)
                {
                    await DispatchBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error processing wide event.");
            }
        }

        // Flush remaining
        if (batch.Count > 0)
            await DispatchBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
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
