namespace Vestige.Internal;

/// <summary>Scoped implementation of <see cref="IWideEventAccessorSetter"/>.</summary>
internal sealed class WideEventAccessor : IWideEventAccessorSetter
{
    private WideEvent? _current;

    /// <inheritdoc/>
    public WideEvent? Current => _current;

    /// <inheritdoc/>
    public void Set(WideEvent ev) => _current = ev;

    /// <inheritdoc/>
    public void Clear() => _current = null;
}
