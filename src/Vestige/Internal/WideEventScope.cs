namespace Vestige.Internal;

/// <summary>Lifecycle wrapper for a background-job wide event.</summary>
internal sealed class WideEventScope(WideEvent ev, IWideEventPipeline pipeline) : IWideEventScope
{
    private int _disposed;

    /// <inheritdoc/>
    public WideEvent Event { get; } = ev;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Event.Outcome ??= "success";
        await pipeline.EmitAsync(Event).ConfigureAwait(false);
    }
}
