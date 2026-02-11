namespace Vestige.Extensions.OpenTelemetry;

/// <summary>Configuration options for <see cref="OpenTelemetrySyncEnricher"/>.</summary>
public sealed class OpenTelemetrySyncOptions
{
    /// <summary>
    /// When <c>true</c>, imports <see cref="System.Diagnostics.Activity.Current"/> tag objects
    /// into the <see cref="WideEvent"/> at event creation time.
    /// This brings in auto-instrumented infrastructure data (DB timings, outgoing HTTP details, etc.)
    /// as queryable wide event fields. Defaults to <c>true</c>.
    /// </summary>
    public bool ReadActivityTags { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, copies all <see cref="WideEvent"/> properties and timings onto
    /// <see cref="System.Diagnostics.Activity.Current"/> as span tags before the event is emitted.
    /// This enriches OTel traces (Jaeger, Honeycomb, Datadog, Tempo) with Vestige business context.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool WriteToActivity { get; set; } = true;

    /// <summary>
    /// Field name prefixes to exclude from syncing in both directions.
    /// For example, <c>["internal."]</c> suppresses any field whose key starts with "internal.".
    /// Defaults to an empty array (no exclusions).
    /// </summary>
    public string[] ExcludePrefixes { get; set; } = [];
}
