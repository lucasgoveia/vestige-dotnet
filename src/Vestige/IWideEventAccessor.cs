namespace Vestige;

/// <summary>
/// Provides access to the ambient <see cref="WideEvent"/> for the current HTTP request scope.
/// Returns <c>null</c> outside of an HTTP request (use <see cref="IWideEventScopeFactory"/> for background jobs).
/// </summary>
public interface IWideEventAccessor
{
    /// <summary>The current wide event, or <c>null</c> if not inside a Vestige-enabled request.</summary>
    WideEvent? Current { get; }
}
