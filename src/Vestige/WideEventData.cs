namespace Vestige;

/// <summary>Serialized form of a <see cref="WideEvent"/>, ready to send to sinks.</summary>
public sealed class WideEventData
{
    /// <summary>Flat dictionary of all fields (fixed + dynamic + timings).</summary>
    public required IReadOnlyDictionary<string, object?> Fields { get; init; }

    /// <summary>Pre-serialized UTF-8 JSON bytes for sinks that pass JSON directly.</summary>
    public required byte[] JsonBytes { get; init; }

    /// <summary>The original <see cref="WideEvent"/> that produced this data.</summary>
    public required WideEvent OriginalEvent { get; init; }
}
