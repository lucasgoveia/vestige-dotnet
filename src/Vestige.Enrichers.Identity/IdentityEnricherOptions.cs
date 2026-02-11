using System.Security.Claims;

namespace Vestige.Enrichers.Identity;

/// <summary>Configuration options for <see cref="IdentityEnricher"/>.</summary>
public sealed class IdentityEnricherOptions
{
    /// <summary>
    /// Maps claim types to wide event field names.
    /// For claim types that may appear multiple times (e.g. roles), all values are collected:
    /// a single value is stored as <c>string</c>; multiple values as <c>string[]</c>.
    /// </summary>
    public Dictionary<string, string> ClaimMappings { get; set; } = new()
    {
        [ClaimTypes.NameIdentifier] = "user.id",
        [ClaimTypes.Name] = "user.name",
        [ClaimTypes.Role] = "user.roles",
    };
}
