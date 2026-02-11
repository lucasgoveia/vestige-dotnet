using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Enrichers.HttpResponse;

/// <summary>Extension methods for registering the HTTP response enricher.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>Add the <see cref="HttpResponseEnricher"/> to stamp HTTP response fields on every event.</summary>
    public static VestigeBuilder AddHttpResponseEnricher(this VestigeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IWideEventEnricher, HttpResponseEnricher>();
        return builder;
    }
}
