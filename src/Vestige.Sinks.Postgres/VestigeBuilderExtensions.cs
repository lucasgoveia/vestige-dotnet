using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Sinks.Postgres;

/// <summary>Extension methods for adding the PostgreSQL sink to Vestige.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>Persist wide events to PostgreSQL.</summary>
    public static VestigeBuilder AddPostgresSink(
        this VestigeBuilder builder,
        Action<PostgresSinkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.AddOptions<PostgresSinkOptions>();

        builder.Services.AddSingleton<IWideEventSink, PostgresSink>();
        return builder;
    }
}
