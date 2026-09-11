namespace Vestige.Internal;

/// <summary>Creates <see cref="WideEventScope"/> instances for background jobs.</summary>
internal sealed class WideEventScopeFactory : IWideEventScopeFactory
{
    private readonly IWideEventFactory _factory;
    private readonly IWideEventPipeline _pipeline;
    private readonly IWideEventEnricher[] _enrichers;

    public WideEventScopeFactory(
        IWideEventFactory factory,
        IWideEventPipeline pipeline,
        IEnumerable<IWideEventEnricher> enrichers)
    {
        _factory = factory;
        _pipeline = pipeline;
        _enrichers = enrichers.ToArray();
    }

    /// <inheritdoc/>
    public IWideEventScope BeginScope(string scopeName)
    {
        var ev = CreateEvent(scopeName);

        foreach (var enricher in _enrichers)
        {
            try
            {
                var task = enricher.OnEventCreatedAsync(ev, CancellationToken.None);
                if (!task.IsCompletedSuccessfully)
                    task.GetAwaiter().GetResult();
            }
            catch { /* enrichers must not break the job */ }
        }

        return new WideEventScope(ev, _pipeline, _enrichers);
    }

    /// <inheritdoc/>
    public async ValueTask<IWideEventScope> BeginScopeAsync(
        string scopeName,
        CancellationToken cancellationToken = default)
    {
        var ev = CreateEvent(scopeName);

        foreach (var enricher in _enrichers)
        {
            try { await enricher.OnEventCreatedAsync(ev, cancellationToken).ConfigureAwait(false); }
            catch { /* enrichers must not break the job */ }
        }

        return new WideEventScope(ev, _pipeline, _enrichers);
    }

    private WideEvent CreateEvent(string scopeName)
    {
        ArgumentNullException.ThrowIfNull(scopeName);
        var ev = _factory.Create();
        ev.Set("scope.name", scopeName);
        return ev;
    }
}
