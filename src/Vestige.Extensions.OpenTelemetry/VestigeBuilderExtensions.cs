using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Extensions.OpenTelemetry;

/// <summary>Extension methods for registering the OpenTelemetry sync enricher.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>
    /// Register the <see cref="OpenTelemetrySyncEnricher"/> for bidirectional sync with OTel Activities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reads IN</b>: At event creation, imports <see cref="System.Diagnostics.Activity.Current"/> tag
    /// objects into the <see cref="WideEvent"/> — auto-instrumented DB timings, outgoing HTTP call details,
    /// etc. become queryable wide event fields without any code changes.
    /// </para>
    /// <para>
    /// <b>Writes OUT</b>: Before emit, copies all Vestige fields onto the active Activity as span tags,
    /// so your OTel backend (Jaeger, Honeycomb, Datadog, Tempo) shows rich business context on traces.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Vestige builder.</param>
    /// <param name="configure">Optional delegate to customise sync behaviour.</param>
    public static VestigeBuilder AddOpenTelemetrySync(
        this VestigeBuilder builder,
        Action<OpenTelemetrySyncOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddSingleton<IWideEventEnricher, OpenTelemetrySyncEnricher>();
        return builder;
    }
}
