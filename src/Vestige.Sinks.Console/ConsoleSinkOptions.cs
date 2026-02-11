namespace Vestige.Sinks.Console;

/// <summary>Configuration options for <see cref="ConsoleSink"/>.</summary>
public sealed class ConsoleSinkOptions
{
    /// <summary>
    /// When true, JSON output is indented for readability.
    /// Default: false (compact).
    /// </summary>
    public bool Indented { get; set; }
}
