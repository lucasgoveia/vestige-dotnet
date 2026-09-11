namespace Vestige;

/// <summary>
/// Caps applied to a <see cref="WideEvent"/> as fields are added, so that a single
/// pathological event cannot exhaust memory or overflow a sink's column bounds.
/// </summary>
public sealed class WideEventLimits
{
    /// <summary>
    /// Shared defaults used when no explicit limits are configured. This single instance backs
    /// every <see cref="WideEvent"/> created without explicit limits, so treat it as read-only —
    /// configure <see cref="VestigeOptions.Limits"/> instead of mutating this.
    /// </summary>
    public static readonly WideEventLimits Default = new();

    /// <summary>
    /// Maximum number of dynamic properties retained. Fields beyond this are discarded and
    /// counted in the <c>vestige.dropped_fields</c> field. Default: 256.
    /// </summary>
    public int MaxFieldCount { get; set; } = 256;

    /// <summary>Maximum key length. Longer keys are truncated. Default: 256.</summary>
    public int MaxKeyLength { get; set; } = 256;

    /// <summary>Maximum length of a <see cref="string"/> value. Longer values are truncated. Default: 4096.</summary>
    public int MaxValueLength { get; set; } = 4096;

    /// <summary>
    /// Maximum length of the <c>error.stack_trace</c> field captured by
    /// <see cref="WideEvent.CaptureException"/>. Default: 8192.
    /// </summary>
    public int MaxStackTraceLength { get; set; } = 8192;

    /// <summary>Suffix appended to any value or key that was truncated.</summary>
    public const string TruncationSuffix = "…[truncated]";

    internal string TruncateKey(string key)
        => key.Length <= MaxKeyLength ? key : Cut(key, MaxKeyLength);

    internal object? TruncateValue(object? value)
        => value is string s && s.Length > MaxValueLength ? Cut(s, MaxValueLength) : value;

    internal string? TruncateStackTrace(string? stackTrace)
        => stackTrace is not null && stackTrace.Length > MaxStackTraceLength
            ? Cut(stackTrace, MaxStackTraceLength)
            : stackTrace;

    /// <summary>
    /// Cut to <paramref name="length"/> UTF-16 units without splitting a surrogate pair — an
    /// orphaned high surrogate would be written to JSON as U+FFFD, mangling the final character.
    /// </summary>
    private static string Cut(string value, int length)
    {
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;

        return string.Concat(value.AsSpan(0, length), TruncationSuffix);
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFieldCount, 1, nameof(MaxFieldCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxKeyLength, 1, nameof(MaxKeyLength));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxValueLength, 1, nameof(MaxValueLength));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStackTraceLength, 1, nameof(MaxStackTraceLength));
    }
}
