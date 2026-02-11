namespace Vestige;

/// <summary>Creates new <see cref="WideEvent"/> instances pre-populated with service-level fixed fields.</summary>
public interface IWideEventFactory
{
    /// <summary>Create a fresh <see cref="WideEvent"/> initialized from <see cref="VestigeOptions"/>.</summary>
    WideEvent Create();
}
