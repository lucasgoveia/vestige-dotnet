using Microsoft.Extensions.Logging;

namespace Vestige.Retention;

/// <summary>Runs retention passes against a store on a fixed schedule.</summary>
public sealed class RetentionLoop
{
    /// <summary>Rows per delete; below SQL Server's 5000-lock escalation threshold.</summary>
    public const int BatchSize = 4000;

    /// <summary>Pause between full batches so purges never monopolise the table.</summary>
    public static readonly TimeSpan BatchPause = TimeSpan.FromMilliseconds(100);

    /// <summary>Delay before the first cycle after startup.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    /// <summary>Delay between cycles.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IReadOnlyList<RetentionPass> _passes;
    private readonly IRetentionStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public RetentionLoop(
        IReadOnlyList<RetentionPass> passes,
        IRetentionStore store,
        ILogger logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _passes = passes;
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>Run cycles until <paramref name="cancellationToken"/> is cancelled. Never throws on cancellation.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InitialDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                try
                {
                    await RunCycleAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    // Drivers differ: SqlClient faults a cancelled command with a SqlException, not an OCE.
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Vestige retention cycle failed.");
                }

                await Task.Delay(Interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Run one cycle now: take the lock, then every pass in order.</summary>
    public async Task<RetentionCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var lease = await _store.TryAcquireLockAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            _logger.LogDebug("Vestige retention skipped: another instance holds the retention lock.");
            return RetentionCycleResult.Skipped;
        }

        var started = _timeProvider.GetTimestamp();
        var deleted = new long[_passes.Count];

        for (var i = 0; i < _passes.Count; i++)
        {
            while (true)
            {
                var rows = await _store.DeleteBatchAsync(_passes[i], BatchSize, cancellationToken).ConfigureAwait(false);
                deleted[i] += rows;
                if (rows < BatchSize)
                    break;

                await Task.Delay(BatchPause, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        var total = deleted.Sum();
        if (total > 0)
        {
            _logger.LogInformation(
                "Vestige retention deleted {Deleted} rows ({PerPass}) in {ElapsedMs} ms.",
                total,
                string.Join(", ", _passes.Select((p, i) => $"{p.Period.TotalDays:0.##}d: {deleted[i]}")),
                (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds);
        }

        return new RetentionCycleResult(true, deleted);
    }
}
