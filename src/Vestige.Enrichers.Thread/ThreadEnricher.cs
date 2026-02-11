namespace Vestige.Enrichers.Thread;

/// <summary>
/// Enriches events with thread context fields captured at event creation:
/// <c>thread.id</c>, <c>thread.name</c>, <c>thread.pool_count</c>.
/// </summary>
public sealed class ThreadEnricher : WideEventEnricherBase
{
    /// <inheritdoc/>
    public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default)
    {
        var thread = System.Threading.Thread.CurrentThread;
        ev.Set("thread.id", thread.ManagedThreadId);

        if (thread.Name is { } name)
            ev.Set("thread.name", name);

        ev.Set("thread.pool_count", System.Threading.ThreadPool.ThreadCount);

        return Task.CompletedTask;
    }
}
