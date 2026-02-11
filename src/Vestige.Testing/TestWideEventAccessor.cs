namespace Vestige.Testing;

/// <summary>
/// Creates and holds a real <see cref="WideEvent"/> for testing code that depends on
/// <see cref="IWideEventAccessor.Current"/> being non-null.
/// </summary>
public sealed class TestWideEventAccessor : IWideEventAccessor
{
    /// <summary>Initializes a new accessor with a fresh <see cref="WideEvent"/>.</summary>
    public TestWideEventAccessor() => Current = new WideEvent();

    /// <inheritdoc/>
    public WideEvent? Current { get; }
}
