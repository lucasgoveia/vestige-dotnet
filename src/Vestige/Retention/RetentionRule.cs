namespace Vestige.Retention;

/// <summary>Keep events that carry <paramref name="Key"/> for <paramref name="Period"/>.</summary>
public sealed record RetentionRule(string Key, TimeSpan Period);
