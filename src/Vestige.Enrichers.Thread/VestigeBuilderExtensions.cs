using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Enrichers.Thread;

/// <summary>Extension methods for registering the thread enricher.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>
    /// Add the <see cref="ThreadEnricher"/> to stamp thread context fields on every event:
    /// <c>thread.id</c>, <c>thread.name</c> (if named), and <c>thread.pool_count</c>.
    /// </summary>
    public static VestigeBuilder AddThreadEnricher(this VestigeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IWideEventEnricher, ThreadEnricher>();
        return builder;
    }
}
