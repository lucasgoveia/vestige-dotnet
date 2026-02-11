using Microsoft.Extensions.DependencyInjection;

namespace Vestige.Enrichers.Identity;

/// <summary>Extension methods for registering the identity enricher.</summary>
public static class VestigeBuilderExtensions
{
    /// <summary>
    /// Add the <see cref="IdentityEnricher"/> to stamp user identity fields on every event.
    /// Reads <c>user.id</c>, <c>user.name</c>, and <c>user.roles</c> from the authenticated
    /// <see cref="System.Security.Claims.ClaimsPrincipal"/>.
    /// </summary>
    /// <param name="builder">The Vestige builder.</param>
    /// <param name="configure">Optional delegate to customise claim-to-field mappings.</param>
    public static VestigeBuilder AddIdentityEnricher(
        this VestigeBuilder builder,
        Action<IdentityEnricherOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpContextAccessor();
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddSingleton<IWideEventEnricher, IdentityEnricher>();
        return builder;
    }
}
