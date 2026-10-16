namespace Vestige.Retention;

/// <summary>
/// One purge pass: delete events older than <paramref name="Period"/> that carry none of the
/// fields in <paramref name="KeepWhenPresent"/> (those belong to a longer-lived rule).
/// </summary>
public sealed record RetentionPass(TimeSpan Period, IReadOnlyList<string> KeepWhenPresent);
