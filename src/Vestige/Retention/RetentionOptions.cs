namespace Vestige.Retention;

/// <summary>Tiered retention for persistent sinks. Disabled until <see cref="Default"/> is set.</summary>
public sealed class RetentionOptions
{
    private static readonly TimeSpan MaxPeriod = TimeSpan.FromSeconds(int.MaxValue);
    private readonly List<RetentionRule> _rules = [];

    /// <summary>How long events matching no rule are kept. <c>null</c> disables retention.</summary>
    public TimeSpan? Default { get; set; }

    /// <summary>Rules that keep events carrying a field for longer than <see cref="Default"/>.</summary>
    public IReadOnlyList<RetentionRule> Rules => _rules;

    /// <summary>True when a purge loop should run.</summary>
    public bool Enabled => Default is not null;

    /// <summary>Keep events that carry <paramref name="key"/> for <paramref name="period"/>.</summary>
    public RetentionOptions KeepLonger(string key, TimeSpan period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _rules.Add(new RetentionRule(key, period));
        return this;
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> when the configuration is inconsistent.</summary>
    public void Validate()
    {
        if (Default is not { } defaultPeriod)
        {
            if (_rules.Count > 0)
                throw new InvalidOperationException("Retention.KeepLonger requires Retention.Default to be set.");
            return;
        }

        if (defaultPeriod <= TimeSpan.Zero)
            throw new InvalidOperationException("Retention.Default must be positive.");
        if (defaultPeriod > MaxPeriod)
            throw new InvalidOperationException($"Retention.Default cannot exceed {MaxPeriod}.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in _rules)
        {
            if (!seen.Add(rule.Key))
                throw new InvalidOperationException($"Retention rule on '{rule.Key}' is declared more than once.");
            if (rule.Period <= defaultPeriod)
                throw new InvalidOperationException(
                    $"Retention rule on '{rule.Key}' must keep longer than Retention.Default ({defaultPeriod}).");
            if (rule.Period > MaxPeriod)
                throw new InvalidOperationException($"Retention rule on '{rule.Key}' cannot exceed {MaxPeriod}.");
        }
    }
}
