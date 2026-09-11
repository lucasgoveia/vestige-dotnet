using Microsoft.AspNetCore.Http;
using Vestige;
using Vestige.Extensions.AspNetCore;
using Vestige.Internal;

namespace Vestige.Tests;

public sealed class WideEventMiddlewareTests
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

    private sealed class StubFactory : IWideEventFactory
    {
        public WideEvent Create() => new();
    }

    private sealed class RecordingEnricher : WideEventEnricherBase
    {
        public int CreatedCalls { get; private set; }
        public int BeforeEmitCalls { get; private set; }

        public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
        {
            CreatedCalls++;
            return Task.CompletedTask;
        }

        public override Task OnBeforeEmitAsync(WideEvent ev, CancellationToken cancellationToken = default)
        {
            BeforeEmitCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingEnricher : WideEventEnricherBase
    {
        public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("enricher boom");

        public override Task OnBeforeEmitAsync(WideEvent ev, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("enricher boom");
    }

    private static (WideEventMiddleware Middleware, CapturingPipeline Pipeline) Build(
        RequestDelegate next,
        params IWideEventEnricher[] enrichers)
    {
        var pipeline = new CapturingPipeline();
        var middleware = new WideEventMiddleware(next, new StubFactory(), pipeline, enrichers);
        return (middleware, pipeline);
    }

    [Fact]
    public async Task UnhandledException_IsCapturedAsError()
    {
        var (middleware, pipeline) = Build(_ => throw new InvalidOperationException("boom"));

        var context = new DefaultHttpContext();
        var accessor = new WideEventAccessor();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(context, accessor));

        var ev = Assert.Single(pipeline.Events);

        // The exception handler sits above this middleware, so the status is still 200 here.
        // Without explicit capture the event would be emitted as an ordinary success and tail
        // sampling would discard it like any other healthy request.
        Assert.Equal("error", ev.Outcome);
        Assert.Equal(typeof(InvalidOperationException).FullName, ev.Get<string>("error.type"));
        Assert.Equal("boom", ev.Get<string>("error.message"));
    }

    [Fact]
    public async Task UnhandledException_IsKeptByAlwaysKeepErrorsSampler()
    {
        var (middleware, pipeline) = Build(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(new DefaultHttpContext(), new WideEventAccessor()));

        var sampler = new TailSampler([new Sampling.RateSampler(0.0)]);
        var withErrors = new TailSampler([
            new Sampling.AlwaysKeepErrorsSampler(),
            new Sampling.RateSampler(0.0),
        ]);

        var ev = Assert.Single(pipeline.Events);
        Assert.Equal(SamplingDecision.Drop, sampler.Evaluate(ev));
        Assert.Equal(SamplingDecision.Keep, withErrors.Evaluate(ev));
    }

    [Fact]
    public async Task ClientDisconnect_IsRecordedAsCancelled()
    {
        var context = new DefaultHttpContext();
        using var aborted = new CancellationTokenSource();
        context.RequestAborted = aborted.Token;

        var (middleware, pipeline) = Build(_ =>
        {
            aborted.Cancel();
            throw new OperationCanceledException(aborted.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => middleware.InvokeAsync(context, new WideEventAccessor()));

        var ev = Assert.Single(pipeline.Events);
        Assert.Equal("cancelled", ev.Outcome);
    }

    [Fact]
    public async Task SuccessfulRequest_RecordsSuccessAndStatus()
    {
        var (middleware, pipeline) = Build(ctx =>
        {
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(new DefaultHttpContext(), new WideEventAccessor());

        var ev = Assert.Single(pipeline.Events);
        Assert.Equal("success", ev.Outcome);
        Assert.Equal(204, ev.StatusCode);
        Assert.False(ev.Has("error.type"));
    }

    [Fact]
    public async Task ServerErrorStatus_RecordsError()
    {
        var (middleware, pipeline) = Build(ctx =>
        {
            ctx.Response.StatusCode = 503;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(new DefaultHttpContext(), new WideEventAccessor());

        var ev = Assert.Single(pipeline.Events);
        Assert.Equal("error", ev.Outcome);
        Assert.Equal(503, ev.StatusCode);
    }

    [Fact]
    public async Task DurationIsRecorded()
    {
        var (middleware, pipeline) = Build(async _ => await Task.Delay(20));

        await middleware.InvokeAsync(new DefaultHttpContext(), new WideEventAccessor());

        var ev = Assert.Single(pipeline.Events);
        Assert.True(ev.DurationMs >= 10, $"expected a recorded duration, got {ev.DurationMs}");
    }

    [Fact]
    public async Task Accessor_IsSetDuringRequestAndClearedAfter()
    {
        var accessor = new WideEventAccessor();
        WideEvent? seenDuringRequest = null;

        var (middleware, _) = Build(_ =>
        {
            seenDuringRequest = accessor.Current;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(new DefaultHttpContext(), accessor);

        Assert.NotNull(seenDuringRequest);
        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task Accessor_IsClearedEvenWhenRequestThrows()
    {
        var accessor = new WideEventAccessor();
        var (middleware, _) = Build(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(new DefaultHttpContext(), accessor));

        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task Enrichers_RunOnBothHooks()
    {
        var enricher = new RecordingEnricher();
        var (middleware, _) = Build(_ => Task.CompletedTask, enricher);

        await middleware.InvokeAsync(new DefaultHttpContext(), new WideEventAccessor());

        Assert.Equal(1, enricher.CreatedCalls);
        Assert.Equal(1, enricher.BeforeEmitCalls);
    }

    [Fact]
    public async Task ThrowingEnricher_DoesNotBreakTheRequestOrLoseTheEvent()
    {
        var (middleware, pipeline) = Build(_ => Task.CompletedTask, new ThrowingEnricher());

        await middleware.InvokeAsync(new DefaultHttpContext(), new WideEventAccessor());

        Assert.Single(pipeline.Events);
    }
}
