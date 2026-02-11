namespace Vestige;

/// <summary>Outcome of a tail-sampling strategy evaluation.</summary>
public enum SamplingDecision
{
    /// <summary>Emit this event.</summary>
    Keep,

    /// <summary>Discard this event.</summary>
    Drop,

    /// <summary>Pass to the next strategy in the chain.</summary>
    Defer,
}
