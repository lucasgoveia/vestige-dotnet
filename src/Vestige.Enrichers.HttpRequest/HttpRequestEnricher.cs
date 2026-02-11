using Microsoft.AspNetCore.Http;

namespace Vestige.Enrichers.HttpRequest;

/// <summary>
/// Enriches events with HTTP request fields:
/// <c>http.method</c>, <c>http.path</c>, <c>http.query</c>, <c>http.user_agent</c>,
/// <c>http.client_ip</c>, <c>http.scheme</c>, <c>http.host</c>.
/// </summary>
public sealed class HttpRequestEnricher(IHttpContextAccessor httpContextAccessor) : WideEventEnricherBase
{
    /// <inheritdoc/>
    public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx is null)
            return Task.CompletedTask;

        var req = ctx.Request;
        ev.Set("http.method", req.Method);
        ev.Set("http.path", req.Path.Value);
        if (req.QueryString.HasValue)
            ev.Set("http.query", req.QueryString.Value);
        if (req.Headers.TryGetValue("User-Agent", out var ua))
            ev.Set("http.user_agent", ua.ToString());

        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        if (ip is not null)
            ev.Set("http.client_ip", ip);

        ev.Set("http.scheme", req.Scheme);
        ev.Set("http.host", req.Host.Value);

        return Task.CompletedTask;
    }
}
