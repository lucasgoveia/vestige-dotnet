namespace Vestige.Retention;

/// <summary>Turns retention options into ordered purge passes.</summary>
public static class RetentionPlanner
{
    /// <summary>
    /// One pass per distinct period, shortest first. A pass spares rows carrying any field whose
    /// rule keeps longer than the pass period; rows are therefore kept for the longest rule they match.
    /// </summary>
    public static IReadOnlyList<RetentionPass> Plan(RetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.Default is not { } defaultPeriod)
            return [];

        return options.Rules
            .Select(r => r.Period)
            .Append(defaultPeriod)
            .Distinct()
            .Order()
            .Select(period => new RetentionPass(
                period,
                options.Rules.Where(r => r.Period > period).Select(r => r.Key).ToArray()))
            .ToArray();
    }
}
