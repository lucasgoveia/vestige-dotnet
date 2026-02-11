using Microsoft.AspNetCore.Http;

namespace Vestige.Enrichers.HttpResponse;

/// <summary>
/// Enriches events (before emit) with HTTP response fields:
/// <c>http.status_code</c>, <c>http.duration_ms</c>, <c>http.content_type</c>, <c>http.bytes_sent</c>.
/// </summary>
public sealed class HttpResponseEnricher(IHttpContextAccessor httpContextAccessor) : WideEventEnricherBase
{
    /// <inheritdoc/>
    public override Task OnBeforeEmitAsync(WideEvent ev, CancellationToken cancellationToken = default)
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx is null)
            return Task.CompletedTask;

        var resp = ctx.Response;
        ev.StatusCode = resp.StatusCode;
        ev.Set("http.status_code", resp.StatusCode);
        ev.Set("http.duration_ms", ev.DurationMs);

        if (resp.Headers.TryGetValue("Content-Type", out var ct))
            ev.Set("http.content_type", ct.ToString());

        if (resp.ContentLength.HasValue)
            ev.Set("http.bytes_sent", resp.ContentLength.Value);

        return Task.CompletedTask;
    }
}
