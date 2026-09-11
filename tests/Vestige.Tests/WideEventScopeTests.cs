using Microsoft.Extensions.Options;
using Vestige;
using Vestige.Internal;

namespace Vestige.Tests;

public sealed class WideEventScopeTests
{
    private sealed class CapturingPipeline : IWideEventPipeline
    {
        public List<WideEvent> Events { get; } = [];
        public long DroppedEventCount => 0;

        public ValueTask EmitAsync(WideEvent ev, CancellationToken cancellationToken = default)
        {
            Events.Add(ev);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MarkingEnricher(string suffix) : WideEventEnricherBase
    {
        public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
        {
            ev.Set("created." + suffix, true);
            return Task.CompletedTask;
        }

        public override Task OnBeforeEmitAsync(WideEvent ev, CancellationToken cancellationToken = default)
        {
            ev.Set("before_emit." + suffix, true);
            return Task.CompletedTask;
        }
    }

    private sealed class AsyncEnricher : WideEventEnricherBase
    {
        public override async Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await Task.Delay(5, cancellationToken);
            ev.Set("async.created", true);
        }
    }

    private static (WideEventScopeFactory Factory, CapturingPipeline Pipeline) Build(
        params IWideEventEnricher[] enrichers)
    {
        var pipeline = new CapturingPipeline();
        var factory = new WideEventScopeFactory(
            new WideEventFactory(Options.Create(new VestigeOptions { ServiceName = "jobs" })),
            pipeline,
            enrichers);
        return (factory, pipeline);
    }

    [Fact]
    public async Task BeginScope_RunsEnrichersOnBothHooks()
    {
        var (factory, pipeline) = Build(new MarkingEnricher("a"));

        await using (var scope = factory.BeginScope("nightly-rollup"))
        {
            Assert.True(scope.Event.Get<bool>("created.a"));
        }

        var ev = Assert.Single(pipeline.Events);
        Assert.True(ev.Get<bool>("created.a"));
        Assert.True(ev.Get<bool>("before_emit.a"));
    }

    [Fact]
    public async Task BeginScopeAsync_AwaitsAsynchronousEnrichers()
    {
        var (factory, pipeline) = Build(new AsyncEnricher());

        await using (var scope = await factory.BeginScopeAsync("nightly-rollup", TestContext.Current.CancellationToken))
        {
            Assert.True(scope.Event.Get<bool>("async.created"));
        }

        Assert.True(Assert.Single(pipeline.Events).Get<bool>("async.created"));
    }

    [Fact]
    public async Task BeginScope_CompletesAsynchronousEnrichersBeforeReturning()
    {
        var (factory, _) = Build(new AsyncEnricher());

        await using var scope = factory.BeginScope("nightly-rollup");

        Assert.True(scope.Event.Get<bool>("async.created"));
    }

    [Fact]
    public async Task Scope_RecordsDuration()
    {
        var (factory, pipeline) = Build();

        await using (var scope = factory.BeginScope("slow-job"))
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        var ev = Assert.Single(pipeline.Events);

        // Without this, duration-based tail sampling could never fire for background work.
        Assert.True(ev.DurationMs >= 10, $"expected a recorded duration, got {ev.DurationMs}");
    }

    [Fact]
    public async Task Scope_DurationIsKeptWhenSetExplicitly()
    {
        var (factory, pipeline) = Build();

        await using (var scope = factory.BeginScope("job"))
        {
            scope.Event.DurationMs = 1234;
        }

        Assert.Equal(1234, Assert.Single(pipeline.Events).DurationMs);
    }

    [Fact]
    public async Task Scope_SlowJobIsKeptByDurationSampler()
    {
        var (factory, pipeline) = Build();

        await using (var scope = factory.BeginScope("slow-job"))
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        var sampler = new TailSampler([
            new Sampling.SlowRequestSampler(10),
            new Sampling.RateSampler(0.0),
        ]);

        Assert.Equal(SamplingDecision.Keep, sampler.Evaluate(Assert.Single(pipeline.Events)));
    }

    [Fact]
    public async Task Scope_SetsScopeNameAndDefaultOutcome()
    {
        var (factory, pipeline) = Build();

        await using (var _ = factory.BeginScope("nightly-rollup")) { }

        var ev = Assert.Single(pipeline.Events);
        Assert.Equal("nightly-rollup", ev.Get<string>("scope.name"));
        Assert.Equal("success", ev.Outcome);
        Assert.Equal("jobs", ev.ServiceName);
    }

    [Fact]
    public async Task Scope_PreservesExplicitOutcome()
    {
        var (factory, pipeline) = Build();

        await using (var scope = factory.BeginScope("job"))
        {
            scope.Event.CaptureException(new InvalidOperationException("boom"));
        }

        Assert.Equal("error", Assert.Single(pipeline.Events).Outcome);
    }

    [Fact]
    public async Task Scope_DisposedTwice_EmitsOnce()
    {
        var (factory, pipeline) = Build();

        var scope = factory.BeginScope("job");
        await scope.DisposeAsync();
        await scope.DisposeAsync();

        Assert.Single(pipeline.Events);
    }
}
