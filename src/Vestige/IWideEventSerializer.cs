namespace Vestige;

/// <summary>Serializes a <see cref="WideEvent"/> to a <see cref="WideEventData"/> DTO.</summary>
public interface IWideEventSerializer
{
    /// <summary>Build a flat field dictionary and serialize to UTF-8 JSON bytes.</summary>
    WideEventData Serialize(WideEvent ev);
}
