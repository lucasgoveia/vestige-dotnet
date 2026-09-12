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

    [Fact]
    public async Task BufferFull_DropsAndCountsWithoutBlocking()
    {
        var sink = new BlockingSink();
        var pipeline = BuildPipeline(
            [sink],
            options: new PipelineOptions
            {
                BufferCapacity = 2,
                BatchSize = 1,
                OnBufferFull = BufferFullBehavior.Drop,
            });

        try
        {
            await pipeline.StartAsync(CancellationToken.None);

            // Park the consumer inside the sink first — only then does the channel actually fill.
            await pipeline.EmitAsync(new WideEvent());
            await sink.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

            // Writes past the buffer are now lost. TryWrite still reports success in this mode,
            // so the count has to come from the channel's itemDropped callback.
            for (int i = 0; i < 50; i++)
                await pipeline.EmitAsync(new WideEvent());

            Assert.True(pipeline.DroppedEventCount > 0, "expected the full buffer to be reported");
        }
        finally
        {
            // Always unblock the consumer, or a failed assertion strands it forever.
            sink.Release.TrySetResult();
            await pipeline.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DropOldest_AlsoCountsEvictedEvents()
    {
        var sink = new BlockingSink();
        var pipeline = BuildPipeline(
            [sink],
            options: new PipelineOptions
            {
                BufferCapacity = 2,
                BatchSize = 1,
                OnBufferFull = BufferFullBehavior.DropOldest,
            });

        try
        {
            await pipeline.StartAsync(CancellationToken.None);

            await pipeline.EmitAsync(new WideEvent());
            await sink.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

            for (int i = 0; i < 50; i++)
                await pipeline.EmitAsync(new WideEvent());

            Assert.True(pipeline.DroppedEventCount > 0, "evictions must be counted too");
        }
        finally
        {
            sink.Release.TrySetResult();
            await pipeline.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BlockMode_AppliesBackpressureInsteadOfDropping()
    {
        var sink = new BlockingSink();
        var pipeline = BuildPipeline(
            [sink],
            options: new PipelineOptions
            {
                BufferCapacity = 2,
                BatchSize = 1,
                OnBufferFull = BufferFullBehavior.Block,
            });

        try
        {
            await pipeline.StartAsync(CancellationToken.None);

            // Park the consumer inside the sink so the buffer can actually fill.
            await pipeline.EmitAsync(new WideEvent());
            await sink.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

            // Fill the buffer, then confirm the next write parks rather than discarding.
            await pipeline.EmitAsync(new WideEvent());
            await pipeline.EmitAsync(new WideEvent());

            var blocked = pipeline.EmitAsync(new WideEvent()).AsTask();
            var completed = await Task.WhenAny(blocked, Task.Delay(200, TestContext.Current.CancellationToken));

            Assert.NotSame(blocked, completed);
            Assert.Equal(0, pipeline.DroppedEventCount);

            sink.Release.TrySetResult();
            await blocked;
        }
        finally
        {
            sink.Release.TrySetResult();
            await pipeline.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NoDrops_LeavesTheCounterAtZero()
    {
        var sink = new InMemorySink();
        var pipeline = BuildPipeline([sink]);

        await pipeline.StartAsync(CancellationToken.None);
        await pipeline.EmitAsync(new WideEvent());
        await pipeline.StopAsync(CancellationToken.None);

        Assert.Equal(0, pipeline.DroppedEventCount);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    [InlineData(10, 100)] // BatchSize larger than the buffer can never fill
    public void InvalidOptions_FailFastAtConstruction(int bufferCapacity, int batchSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildPipeline(
            [new InMemorySink()],
            options: new PipelineOptions { BufferCapacity = bufferCapacity, BatchSize = batchSize }));
    }

    [Fact]
    public void InvalidFlushInterval_FailsFastAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildPipeline(
            [new InMemorySink()],
            options: new PipelineOptions { BatchFlushInterval = TimeSpan.Zero }));
    }

    [Fact]
    public async Task SinkThatRetainsTheBatch_SeesAStableList()
    {
        var sink = new RetainingSink();
        var pipeline = BuildPipeline(
            [sink],
            options: new PipelineOptions { BatchSize = 2, BatchFlushInterval = TimeSpan.FromMilliseconds(20) });

        await pipeline.StartAsync(CancellationToken.None);

        for (int i = 0; i < 6; i++)
        {
            var ev = new WideEvent();
            ev.Set("index", i);
            await pipeline.EmitAsync(ev);
        }

        await pipeline.StopAsync(CancellationToken.None);

        // Each retained batch must still hold what the sink was handed, not a recycled buffer.
        Assert.All(sink.Retained, b => Assert.NotEmpty(b));
        Assert.Equal(6, sink.Retained.Sum(b => b.Count));
    }

    [Fact]
    public async Task SerializationFailure_DoesNotStopLaterEvents()
    {
        var sink = new InMemorySink();
        var pipeline = new WideEventPipeline(
            new ThrowOnceSerializer(),
            [],
            [sink],
            Options.Create(new PipelineOptions { BatchSize = 1 }),
            NullLogger<WideEventPipeline>.Instance);

        await pipeline.StartAsync(CancellationToken.None);

        await pipeline.EmitAsync(new WideEvent()); // this one throws inside the serializer
        await pipeline.EmitAsync(new WideEvent());
        await pipeline.EmitAsync(new WideEvent());

        await pipeline.StopAsync(CancellationToken.None);

        Assert.Equal(2, sink.Events.Count);
    }

    [Fact]
    public async Task ForcedShutdown_StillFlushesSinks()
    {
        var sink = new CancellationTrackingSink();
        var pipeline = BuildPipeline(
            [sink],
            options: new PipelineOptions
            {
                BatchSize = 1,
                BatchFlushInterval = TimeSpan.FromSeconds(30),
                ShutdownFlushTimeout = TimeSpan.FromSeconds(5),
            });

        await pipeline.StartAsync(CancellationToken.None);
        await pipeline.EmitAsync(new WideEvent { Outcome = "success" });
        await sink.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Shutdown deadline already blown: the flush must still run on its own budget rather than
        // inheriting the cancelled token and failing every sink.
        using var expired = new CancellationTokenSource();
        await expired.CancelAsync();

        await pipeline.StopAsync(expired.Token);

        Assert.Equal(1, sink.FlushCount);
        Assert.False(sink.FlushCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task PartialBatch_FlushDeadlineSurvivesASteadyStreamOfSampledOutEvents()
    {
        var sink = new InMemorySink();
        var pipeline = BuildPipeline(
            [sink],
            [new KeepFirstThenDropSampler()],
            new PipelineOptions { BatchSize = 10, BatchFlushInterval = TimeSpan.FromMilliseconds(150) });

        await pipeline.StartAsync(CancellationToken.None);

        // One kept event sits in a partial batch...
        await pipeline.EmitAsync(new WideEvent());

        // ...while sampled-out traffic keeps arriving. Dropped events must not postpone the
        // deadline of the event already waiting.
        for (int i = 0; i < 15; i++)
        {
            await pipeline.EmitAsync(new WideEvent());
            await Task.Delay(40, TestContext.Current.CancellationToken);
        }

        Assert.Single(sink.Events);

        await pipeline.StopAsync(CancellationToken.None);
    }

    private sealed class KeepFirstThenDropSampler : ISamplingStrategy
    {
        private int _seen;
        public SamplingDecision Evaluate(WideEvent ev)
            => Interlocked.Increment(ref _seen) == 1 ? SamplingDecision.Keep : SamplingDecision.Drop;
    }

    private sealed class ThrowOnceSerializer : IWideEventSerializer
    {
        private int _calls;
        private readonly SystemTextJsonSerializer _inner = new();

        public WideEventData Serialize(WideEvent ev)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("serializer boom");
            return _inner.Serialize(ev);
        }
    }

    private sealed class RetainingSink : IWideEventSink
    {
        public List<IReadOnlyList<WideEventData>> Retained { get; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default)
        {
            Retained.Add(batch);
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSink : IWideEventSink
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task;
        }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellationTrackingSink : IWideEventSink
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken EmitCancellationToken { get; private set; }
        public CancellationToken FlushCancellationToken { get; private set; }
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
        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushCount++;
            FlushCancellationToken = cancellationToken;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
