namespace Vestige;

/// <summary>
/// Creates explicitly scoped wide events for background jobs and hosted services.
/// Unlike <see cref="IWideEventAccessor"/>, scopes created here are always non-null.
/// </summary>
public interface IWideEventScopeFactory
{
    /// <summary>
    /// Begin a new wide event scope named <paramref name="scopeName"/>.
    /// Dispose the returned scope to auto-emit the event.
    /// </summary>
    IWideEventScope BeginScope(string scopeName);
}
