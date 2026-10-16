namespace Vestige.Retention;

/// <summary>Outcome of one retention cycle.</summary>
public sealed record RetentionCycleResult(bool Acquired, IReadOnlyList<long> DeletedPerPass)
{
    /// <summary>The cycle did not run because another instance held the lock.</summary>
    public static RetentionCycleResult Skipped { get; } = new(false, []);

    /// <summary>Rows deleted across all passes.</summary>
    public long TotalDeleted => DeletedPerPass.Sum();
}
