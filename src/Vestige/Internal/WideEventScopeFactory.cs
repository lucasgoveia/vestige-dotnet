namespace Vestige.Internal;

/// <summary>Creates <see cref="WideEventScope"/> instances for background jobs.</summary>
internal sealed class WideEventScopeFactory(IWideEventFactory factory, IWideEventPipeline pipeline) : IWideEventScopeFactory
{
    /// <inheritdoc/>
    public IWideEventScope BeginScope(string scopeName)
    {
        var ev = factory.Create();
        ev.Set("scope.name", scopeName);
        return new WideEventScope(ev, pipeline);
    }
}
