using System.Diagnostics;

namespace Vestige.Enrichers.Activity;

/// <summary>
/// Enriches events with distributed tracing fields from <see cref="System.Diagnostics.Activity.Current"/>:
/// <c>trace_id</c>, <c>span_id</c>, <c>parent_span_id</c>, and <c>baggage.*</c>.
/// </summary>
public sealed class ActivityEnricher : WideEventEnricherBase
{
    /// <inheritdoc/>
    public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
    {
        var activity = System.Diagnostics.Activity.Current;
        if (activity is null)
            return Task.CompletedTask;

        ev.TraceId = activity.TraceId.ToString();
        ev.SpanId = activity.SpanId.ToString();

        if (activity.ParentSpanId != default)
            ev.Set("parent_span_id", activity.ParentSpanId.ToString());

        foreach (var (key, value) in activity.Baggage)
        {
            if (value is not null)
                ev.Set($"baggage.{key}", value);
        }

        return Task.CompletedTask;
    }
}
