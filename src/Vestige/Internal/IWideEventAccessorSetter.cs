using System.ComponentModel;

namespace Vestige.Internal;

/// <summary>
/// Internal contract for setting/clearing the ambient <see cref="WideEvent"/> on the scoped accessor.
/// Public so that <c>Vestige.Extensions.AspNetCore</c> can resolve it from DI without InternalsVisibleTo.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IWideEventAccessorSetter : IWideEventAccessor
{
    /// <summary>Set the current event for this scope.</summary>
    void Set(WideEvent ev);

    /// <summary>Clear the current event (called after pipeline emit).</summary>
    void Clear();
}
