using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Vestige.Extensions.Hosting;

/// <summary>Extension methods for adding Vestige hosting support.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>
    /// Add hosting support for Vestige (background job scope factory).
    /// The core package already registers the pipeline and scope factory;
    /// this extension is provided for explicit opt-in and future extensibility.
    /// </summary>
    public static VestigeBuilder AddHostingSupport(this VestigeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHostedService<VestigePipelineHostedService>();
        return builder;
    }
}
