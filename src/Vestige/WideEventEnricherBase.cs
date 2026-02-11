namespace Vestige;

/// <summary>
/// Base class for enrichers that only implement one lifecycle hook.
/// Provides no-op defaults for both hooks.
/// </summary>
public abstract class WideEventEnricherBase : IWideEventEnricher
{
    /// <inheritdoc/>
    public virtual Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public virtual Task OnBeforeEmitAsync(WideEvent ev, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
