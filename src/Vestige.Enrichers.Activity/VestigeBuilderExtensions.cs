using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Enrichers.Activity;

/// <summary>Extension methods for registering the Activity/OTel enricher.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>Add the <see cref="ActivityEnricher"/> to stamp trace/span IDs and baggage on every event.</summary>
    public static VestigeBuilder AddActivityEnricher(this VestigeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IWideEventEnricher, ActivityEnricher>();
        return builder;
    }
}
