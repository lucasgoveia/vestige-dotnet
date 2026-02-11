using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Enrichers.Environment;

/// <summary>Extension methods for registering the environment enricher.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>Add the <see cref="EnvironmentEnricher"/> to stamp host/runtime fields on every event.</summary>
    public static VestigeBuilder AddEnvironmentEnricher(this VestigeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IWideEventEnricher, EnvironmentEnricher>();
        return builder;
    }
}
