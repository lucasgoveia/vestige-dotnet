using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Enrichers.HttpRequest;

/// <summary>Extension methods for registering the HTTP request enricher.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>Add the <see cref="HttpRequestEnricher"/> to stamp HTTP request fields on every event.</summary>
    public static VestigeBuilder AddHttpRequestEnricher(this VestigeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IWideEventEnricher, HttpRequestEnricher>();
        return builder;
    }
}
