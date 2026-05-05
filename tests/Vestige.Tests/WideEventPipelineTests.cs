using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vestige;
using Vestige.Internal;
using Vestige.Sampling;
using Vestige.Testing;

namespace Vestige.Tests;

public sealed class WideEventPipelineTests
{
    private static WideEventPipeline BuildPipeline(
        IEnumerable<IWideEventSink> sinks,
        IEnumerable<ISamplingStrategy>? strategies = null,
        PipelineOptions? options = null)
    {
        return new WideEventPipeline(
            new SystemTextJsonSerializer(),
            strategies ?? [],
            sinks,
            Options.Create(options ?? new PipelineOptions()),
            NullLogger<WideEventPipeline>.Instance);
    }

    [Fact]
    public async Task Emit_ReachesInMemorySink()
    {
        var sink = new InMemorySink();
        var pipeline = BuildPipeline([sink]);

        await pipeline.StartAsync(CancellationToken.None);

        var ev = new WideEvent { ServiceName = "test", Outcome = "success" };
        await pipeline.EmitAsync(ev);

        await pipeline.StopAsync(CancellationToken.None);

        Assert.Single(sink.Events);
        Assert.Equal(ev, sink.Events[0].OriginalEvent);
    }

    [Fact]
    public async Task SamplingDrop_PreventsReachingSink()
    {
        var sink = new InMemorySink();
        var pipeline = BuildPipeline(
            [sink],
            [new RateSampler(0.0)]); // drop everything

        await pipeline.StartAsync(CancellationToken.None);

        var ev = new WideEvent { Outcome = "success" };
        await pipeline.EmitAsync(ev);

        await pipeline.StopAsync(CancellationToken.None);

        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task GracefulShutdown_FlushesBufferedEvents()
    {
        var sink = new InMemorySink();
        var pipeline = BuildPipeline([sink]);

        await pipeline.StartAsync(CancellationToken.None);

        for (int i = 0; i < 5; i++)
        {
            var ev = new WideEvent { Outcome = "success" };
            ev.Set("index", i);
            await pipeline.EmitAsync(ev);
        }

        await pipeline.StopAsync(CancellationToken.None);

        Assert.Equal(5, sink.Events.Count);
    }

    [Fact]
    public async Task MultipleSinks_AllReceiveEvents()
    {
        var sink1 = new InMemorySink();
        var sink2 = new InMemorySink();
        var pipeline = BuildPipeline([sink1, sink2]);

        await pipeline.StartAsync(CancellationToken.None);

        var ev = new WideEvent { Outcome = "success" };
        await pipeline.EmitAsync(ev);

        await pipeline.StopAsync(CancellationToken.None);

        Assert.Single(sink1.Events);
        Assert.Single(sink2.Events);
    }

    [Fact]
    public async Task PartialBatch_FlushesOnConfiguredInterval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sink = new InMemorySink();
        var pipeline = BuildPipeline(
            [sink],
            options: new PipelineOptions
            {
                BatchSize = 10,
                BatchFlushInterval = TimeSpan.FromMilliseconds(50),
            });

        await pipeline.StartAsync(cancellationToken);

        var ev = new WideEvent { Outcome = "success" };
        await pipeline.EmitAsync(ev, cancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(2000), cancellationToken);

        Assert.Single(sink.Events);

        await pipeline.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task StopAsync_DrainsBufferedEvents_WhenShutdownTokenHasNotExpired()
    {
        var sink = new CancellationTrackingSink();
        var pipeline = BuildPipeline(
            [sink],
            options: new PipelineOptions
            {
                BatchSize = 1,
                BatchFlushInterval = TimeSpan.FromSeconds(30),
            });

        await pipeline.StartAsync(CancellationToken.None);
        await pipeline.EmitAsync(new WideEvent { Outcome = "success" });
        await sink.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stopTask = pipeline.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        Assert.False(stopTask.IsCompleted);
        Assert.False(sink.EmitCancellationToken.IsCancellationRequested);

        sink.Release.TrySetResult();
        await stopTask;

        Assert.Equal(1, sink.EmitCount);
        Assert.False(sink.EmitCancellationToken.IsCancellationRequested);
        Assert.Equal(1, sink.FlushCount);
    }

    private sealed class CancellationTrackingSink : IWideEventSink
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken EmitCancellationToken { get; private set; }
        public int EmitCount { get; private set; }
        public int FlushCount { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default)
        {
            EmitCount++;
            EmitCancellationToken = cancellationToken;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        public Task FlushAsync(CancellationToken cancellationToken = default) { FlushCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
