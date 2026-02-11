using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vestige.Internal;

namespace Vestige;

/// <summary>Entry-point DI extensions for registering Vestige services.</summary>
public static class VestigeServiceCollectionExtensions
{
    /// <summary>
    /// Register Vestige core services and return a <see cref="VestigeBuilder"/> for further configuration.
    /// </summary>
    public static VestigeBuilder AddVestige(this IServiceCollection services, Action<VestigeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
            services.Configure(configure);

        services.AddOptions<VestigeOptions>();
        services.AddOptions<PipelineOptions>();

        // Scoped accessor — same instance per DI scope (HTTP request)
        services.AddScoped<WideEventAccessor>();
        services.AddScoped<IWideEventAccessor>(sp => sp.GetRequiredService<WideEventAccessor>());
        services.AddScoped<IWideEventAccessorSetter>(sp => sp.GetRequiredService<WideEventAccessor>());

        services.TryAddSingleton<IWideEventFactory, WideEventFactory>();
        services.TryAddSingleton<IWideEventSerializer, SystemTextJsonSerializer>();
        services.TryAddSingleton<IWideEventScopeFactory, WideEventScopeFactory>();

        // Pipeline is both the interface and a hosted service (background consumer)
        services.TryAddSingleton<WideEventPipeline>();
        services.TryAddSingleton<IWideEventPipeline>(sp => sp.GetRequiredService<WideEventPipeline>());
        services.AddHostedService(sp => sp.GetRequiredService<WideEventPipeline>());

        return new VestigeBuilder(services);
    }
}
