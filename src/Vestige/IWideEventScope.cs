namespace Vestige;

/// <summary>
/// Represents an explicitly managed wide event scope for background jobs.
/// The event is auto-emitted when the scope is disposed.
/// </summary>
public interface IWideEventScope : IAsyncDisposable
{
    /// <summary>The wide event for this scope. Always non-null.</summary>
    WideEvent Event { get; }
}
