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
}
