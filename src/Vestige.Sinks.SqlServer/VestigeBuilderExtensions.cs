using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Sinks.SqlServer;

/// <summary>Extension methods for adding the SQL Server sink to Vestige.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>Persist wide events to SQL Server.</summary>
    public static VestigeBuilder AddSqlServerSink(
        this VestigeBuilder builder,
        Action<SqlServerSinkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.AddOptions<SqlServerSinkOptions>();

        builder.Services.AddSingleton<IWideEventSink, SqlServerSink>();
        return builder;
    }
}
