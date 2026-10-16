namespace Vestige.Sinks.SqlServer;

/// <summary>A wide-event field copied into its own typed column.</summary>
/// <param name="Key">Field name exactly as passed to <c>WideEvent.Set</c> (ordinal, case-sensitive).</param>
/// <param name="Type">Column type.</param>
/// <param name="Column">Column name.</param>
/// <param name="Indexed">Create <c>([Column], [timestamp_utc] DESC)</c>.</param>
public sealed record PromotedField(string Key, PromotedType Type, string Column, bool Indexed);
