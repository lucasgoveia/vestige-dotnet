namespace Vestige.Testing;

/// <summary>
/// Always returns null from <see cref="IWideEventAccessor.Current"/>.
/// Use in unit tests where the code under test calls <c>ev?.Set()</c> defensively.
/// </summary>
public sealed class NullWideEventAccessor : IWideEventAccessor
{
    /// <summary>A shared singleton instance.</summary>
    public static readonly NullWideEventAccessor Instance = new();

    /// <inheritdoc/>
    public WideEvent? Current => null;
}
