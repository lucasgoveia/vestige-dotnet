namespace Vestige.Sinks.Console;

/// <summary>Configuration options for <see cref="ConsoleSink"/>.</summary>
public sealed class ConsoleSinkOptions
{
    /// <summary>
    /// When true, JSON output is indented for readability.
    /// Default: false (compact).
    /// </summary>
    public bool Indented { get; set; }

    /// <summary>
    /// Size of the write buffer, in bytes. Output is flushed once per batch rather than per event.
    /// Values below 4096 are raised to 4096. Default: 32,768.
    /// </summary>
    public int BufferSizeBytes { get; set; } = 32 * 1024;
}
