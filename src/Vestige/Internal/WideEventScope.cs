using System.Diagnostics;

namespace Vestige.Internal;

/// <summary>Lifecycle wrapper for a background-job wide event.</summary>
internal sealed class WideEventScope : IWideEventScope
{
    private readonly IWideEventPipeline _pipeline;
    private readonly IWideEventEnricher[] _enrichers;
    private readonly long _startedAt;
    private int _disposed;

    public WideEventScope(WideEvent ev, IWideEventPipeline pipeline, IWideEventEnricher[] enrichers)
    {
        Event = ev;
        _pipeline = pipeline;
        _enrichers = enrichers;
        _startedAt = Stopwatch.GetTimestamp();
    }

    /// <inheritdoc/>
    public WideEvent Event { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Background scopes are timed here; without this, duration-based tail sampling
        // (AlwaysKeepSlowRequests) could never fire outside an HTTP request.
        if (Event.DurationMs == 0)
            Event.DurationMs = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;

        Event.TrySetOutcome("success");

        foreach (var enricher in _enrichers)
        {
            try { await enricher.OnBeforeEmitAsync(Event, CancellationToken.None).ConfigureAwait(false); }
            catch { /* enrichers must not block emit */ }
        }

        try { await _pipeline.EmitAsync(Event).ConfigureAwait(false); }
        catch { /* pipeline must not throw */ }
    }
}
