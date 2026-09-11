using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Vestige.Internal;

namespace Vestige.Extensions.AspNetCore;

/// <summary>
/// ASP.NET Core middleware that creates a <see cref="WideEvent"/> per request,
/// runs enrichers, and emits the event to the pipeline after the response.
/// </summary>
public sealed class WideEventMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWideEventFactory _factory;
    private readonly IWideEventPipeline _pipeline;
    private readonly IWideEventEnricher[] _enrichers;

    public WideEventMiddleware(
        RequestDelegate next,
        IWideEventFactory factory,
        IWideEventPipeline pipeline,
        IEnumerable<IWideEventEnricher> enrichers)
    {
        _next = next;
        _factory = factory;
        _pipeline = pipeline;
        _enrichers = enrichers.ToArray();
    }

    public async Task InvokeAsync(HttpContext context, IWideEventAccessorSetter accessor)
    {
        var ev = _factory.Create();
        accessor.Set(ev);

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            foreach (var enricher in _enrichers)
            {
                try { await enricher.OnEventCreatedAsync(ev, context.RequestAborted).ConfigureAwait(false); }
                catch { /* enrichers must not break the request */ }
            }

            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // A client disconnect is not a server fault; record it as its own outcome.
            ev.TrySetOutcome("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // This middleware sits below the exception handler, so the response status is still
            // 200 at this point. Without capturing here the event would be emitted as a success
            // and tail sampling would treat it as ordinary traffic.
            ev.CaptureException(ex);
            throw;
        }
        finally
        {
            ev.DurationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            if (context.Response.StatusCode > 0)
                ev.StatusCode = context.Response.StatusCode;

            ev.TrySetOutcome(context.Response.StatusCode >= 500 ? "error" : "success");

            foreach (var enricher in _enrichers)
            {
                try { await enricher.OnBeforeEmitAsync(ev, CancellationToken.None).ConfigureAwait(false); }
                catch { /* enrichers must not block emit */ }
            }

            try { await _pipeline.EmitAsync(ev).ConfigureAwait(false); }
            catch { /* pipeline must not throw */ }

            accessor.Clear();
        }
    }
}
