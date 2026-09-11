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
    /// <remarks>
    /// Runs <see cref="IWideEventEnricher.OnEventCreatedAsync"/> for every registered enricher,
    /// blocking if one completes asynchronously. Prefer <see cref="BeginScopeAsync"/>.
    /// </remarks>
    IWideEventScope BeginScope(string scopeName);

    /// <summary>
    /// Begin a new wide event scope named <paramref name="scopeName"/>, awaiting the registered
    /// <see cref="IWideEventEnricher.OnEventCreatedAsync"/> hooks. Dispose the scope to auto-emit.
    /// </summary>
    ValueTask<IWideEventScope> BeginScopeAsync(string scopeName, CancellationToken cancellationToken = default);
}
