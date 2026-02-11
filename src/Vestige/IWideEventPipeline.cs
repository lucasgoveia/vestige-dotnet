namespace Vestige;

/// <summary>
/// Internal pipeline that accepts completed wide events, serializes them,
/// applies tail sampling, and dispatches to registered sinks.
/// </summary>
public interface IWideEventPipeline
{
    /// <summary>Submit a completed event for processing.</summary>
    ValueTask EmitAsync(WideEvent ev, CancellationToken cancellationToken = default);
}
