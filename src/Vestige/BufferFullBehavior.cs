namespace Vestige;

/// <summary>Behavior when the pipeline channel buffer is full.</summary>
public enum BufferFullBehavior
{
    /// <summary>Drop the incoming event silently.</summary>
    Drop,

    /// <summary>Block the caller until space is available.</summary>
    Block,

    /// <summary>Drop the oldest buffered event to make room.</summary>
    DropOldest,
}
