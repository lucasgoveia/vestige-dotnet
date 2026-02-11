using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Sinks.Console;

/// <summary>Extension methods for adding the console sink to Vestige.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>Write all wide events as JSON lines to stdout.</summary>
    public static VestigeBuilder AddConsoleSink(
        this VestigeBuilder builder,
        Action<ConsoleSinkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.AddOptions<ConsoleSinkOptions>();

        builder.Services.AddSingleton<IWideEventSink, ConsoleSink>();
        return builder;
    }
}
