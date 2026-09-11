namespace Vestige;

/// <summary>Serialized form of a <see cref="WideEvent"/>, ready to send to sinks.</summary>
/// <remarks>Immutable once constructed, and safe to retain and share across sinks and threads.</remarks>
public sealed class WideEventData
{
    /// <summary>
    /// Flat dictionary of all fields (fixed + dynamic + timings), captured as a point-in-time
    /// snapshot of the originating event.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Fields { get; init; }

    /// <summary>Pre-serialized UTF-8 JSON bytes for sinks that pass JSON directly.</summary>
    public required byte[] JsonBytes { get; init; }

    /// <summary>
    /// The original <see cref="WideEvent"/> that produced this data. Provided for sinks that need
    /// the live object; prefer <see cref="Fields"/>, which cannot change underneath you.
    /// </summary>
    public required WideEvent OriginalEvent { get; init; }
}
