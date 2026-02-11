using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Vestige.Extensions.OpenTelemetry;

/// <summary>
/// Bidirectional sync between OTel <see cref="Activity"/> tags and <see cref="WideEvent"/> fields.
/// <list type="bullet">
///   <item>
///     <description>
///       At event creation: imports existing <see cref="Activity.Current"/> tag objects into the
///       <see cref="WideEvent"/>, pulling in auto-instrumented infrastructure data (DB timings,
///       outgoing HTTP details, etc.) as queryable fields.
///     </description>
///   </item>
///   <item>
///     <description>
///       Before emit: copies all <see cref="WideEvent"/> properties and timings onto the
///       <see cref="Activity.Current"/> as span tags, enriching OTel backends with business context.
///     </description>
///   </item>
/// </list>
/// </summary>
public sealed class OpenTelemetrySyncEnricher(IOptions<OpenTelemetrySyncOptions> options) : WideEventEnricherBase
{
    private readonly OpenTelemetrySyncOptions _options = options.Value;

    /// <inheritdoc/>
    public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        if (activity is null || !_options.ReadActivityTags)
            return Task.CompletedTask;

        foreach (var tag in activity.TagObjects)
        {
            if (IsExcluded(tag.Key)) continue;
            ev.Set(tag.Key, tag.Value);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task OnBeforeEmitAsync(WideEvent ev, CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        if (activity is null || !_options.WriteToActivity)
            return Task.CompletedTask;

        foreach (var (key, value) in ev.Properties)
        {
            if (IsExcluded(key)) continue;
            activity.SetTag(key, value);
        }

        return Task.CompletedTask;
    }

    private bool IsExcluded(string key)
    {
        var prefixes = _options.ExcludePrefixes;
        if (prefixes.Length == 0) return false;
        foreach (var prefix in prefixes)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }
}
