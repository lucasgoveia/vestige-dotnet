using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Vestige.Enrichers.Identity;

/// <summary>
/// Enriches events with user identity fields read from <see cref="System.Security.Claims.ClaimsPrincipal"/>
/// via <see cref="IHttpContextAccessor"/>: <c>user.id</c>, <c>user.name</c>, <c>user.roles</c>,
/// plus any additional mappings configured in <see cref="IdentityEnricherOptions.ClaimMappings"/>.
/// Only fires for authenticated principals.
/// </summary>
public sealed class IdentityEnricher(
    IHttpContextAccessor httpContextAccessor,
    IOptions<IdentityEnricherOptions> options) : WideEventEnricherBase
{
    private readonly IdentityEnricherOptions _options = options.Value;

    /// <inheritdoc/>
    public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        foreach (var (claimType, fieldName) in _options.ClaimMappings)
        {
            var values = user.FindAll(claimType)
                             .Select(c => c.Value)
                             .ToArray();

            if (values.Length == 0) continue;

            // Store single values as plain strings; multiple as string[].
            ev.Set(fieldName, values.Length == 1 ? (object)values[0] : values);
        }

        return Task.CompletedTask;
    }
}
