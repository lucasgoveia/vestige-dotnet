namespace Vestige.Retention;

/// <summary>Storage-specific half of retention: a cross-instance lock and one batched delete.</summary>
public interface IRetentionStore
{
    /// <summary>
    /// Try to become the single instance purging this table. Returns a lease that releases the
    /// lock when disposed, or <c>null</c> when another instance holds it.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken);

    /// <summary>Delete at most <paramref name="batchSize"/> rows matching <paramref name="pass"/>; return rows deleted.</summary>
    Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken cancellationToken);
}
