using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Vestige.Retention;
using Vestige.Tests.TestSupport;

namespace Vestige.Tests;

public sealed class RetentionLoopTests
{
    private static readonly RetentionPass Short = new(TimeSpan.FromDays(30), ["audit_action"]);
    private static readonly RetentionPass Long = new(TimeSpan.FromDays(365), []);

    [Fact]
    public async Task RunCycle_LockHeldElsewhere_SkipsWithoutDeleting()
    {
        var store = new FakeStore { LockAvailable = false };
        var loop = new RetentionLoop([Short, Long], store, new ListLogger<RetentionLoop>(), TimeProvider.System);

        var result = await loop.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Acquired);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task RunCycle_RunsPassesInOrder_UntilBatchIsShort()
    {
        var store = new FakeStore();
        store.Results[Short] = new Queue<int>([4000, 4000, 12]);
        store.Results[Long] = new Queue<int>([0]);
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short, Long], store, logger, TimeProvider.System);

        var result = await loop.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Acquired);
        Assert.Equal(new[] { 8012L, 0L }, result.DeletedPerPass);
        Assert.Equal(8012L, result.TotalDeleted);
        Assert.Equal(new[] { Short, Short, Short, Long }, store.Calls);
        Assert.All(store.BatchSizes, size => Assert.Equal(RetentionLoop.BatchSize, size));
        Assert.Equal(1, store.LocksReleased);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("8012"));
    }

    [Fact]
    public async Task RunCycle_NothingDeleted_LogsNoInformation()
    {
        var store = new FakeStore();
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short], store, logger, TimeProvider.System);

        await loop.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task RunCycle_DeleteThrows_ReleasesLockAndRethrows()
    {
        var store = new FakeStore { DeleteFailure = new InvalidOperationException("boom") };
        var loop = new RetentionLoop([Short], store, new ListLogger<RetentionLoop>(), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.RunCycleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, store.LocksReleased);
    }

    [Fact]
    public async Task RunAsync_WaitsInitialDelay_ThenRepeatsEveryInterval()
    {
        var time = new FakeTimeProvider();
        var store = new FakeStore { LockAvailable = false };
        var loop = new RetentionLoop([Short], store, new ListLogger<RetentionLoop>(), time);
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);

        time.Advance(RetentionLoop.InitialDelay - TimeSpan.FromSeconds(1));
        Assert.False(await store.LockAttempts.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));

        Assert.True(await AdvanceUntilAsync(time, TimeSpan.FromSeconds(1), store.LockAttempts));
        Assert.True(await AdvanceUntilAsync(time, RetentionLoop.Interval, store.LockAttempts));

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task RunAsync_CycleFails_LogsErrorAndKeepsRunning()
    {
        var time = new FakeTimeProvider();
        var store = new FakeStore { LockFailure = new InvalidOperationException("db down") };
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short], store, logger, time);
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);

        Assert.True(await AdvanceUntilAsync(time, RetentionLoop.InitialDelay, store.LockAttempts));
        Assert.True(await AdvanceUntilAsync(time, RetentionLoop.Interval, store.LockAttempts));
        cts.Cancel();
        await run;

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception?.Message == "db down");
    }

    [Fact]
    public async Task RunAsync_CancelledDuringInitialDelay_CompletesQuietly()
    {
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short], new FakeStore(), logger, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);
        cts.Cancel();
        await run;

        Assert.True(run.IsCompletedSuccessfully);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task RunAsync_CancelledMidCycle_CompletesQuietly()
    {
        var time = new FakeTimeProvider();
        var store = new FakeStore { BlockDeletes = true };
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short], store, logger, time);
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);
        Assert.True(await AdvanceUntilAsync(time, RetentionLoop.InitialDelay, store.LockAttempts));
        Assert.True(await store.DeleteStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        cts.Cancel();
        await run;

        Assert.True(run.IsCompletedSuccessfully);
        Assert.Equal(1, store.LocksReleased);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task RunAsync_CancelledMidCycle_DriverFaultsWithNonCancellationException_CompletesQuietly()
    {
        var time = new FakeTimeProvider();
        var store = new FakeStore { BlockDeletes = true, CancelAs = new InvalidOperationException("Operation cancelled by user.") };
        var logger = new ListLogger<RetentionLoop>();
        var loop = new RetentionLoop([Short], store, logger, time);
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);
        Assert.True(await AdvanceUntilAsync(time, RetentionLoop.InitialDelay, store.LockAttempts));
        Assert.True(await store.DeleteStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        cts.Cancel();
        await run;

        Assert.True(run.IsCompletedSuccessfully);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    // FakeTimeProvider only fires timers that already exist, and the loop registers its next
    // delay asynchronously, so keep advancing until the expected cycle is observed. Overshooting
    // can trigger extra cycles, which these tests tolerate.
    private static async Task<bool> AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, SemaphoreSlim signal)
    {
        for (var i = 0; i < 50; i++)
        {
            time.Advance(step);
            if (await signal.WaitAsync(TimeSpan.FromMilliseconds(100)))
                return true;
        }

        return false;
    }

    private sealed class FakeStore : IRetentionStore
    {
        public bool LockAvailable { get; init; } = true;
        public Exception? LockFailure { get; init; }
        public Exception? DeleteFailure { get; init; }
        public bool BlockDeletes { get; init; }
        public Exception? CancelAs { get; init; }
        public Dictionary<RetentionPass, Queue<int>> Results { get; } = [];
        public List<RetentionPass> Calls { get; } = [];
        public List<int> BatchSizes { get; } = [];
        public SemaphoreSlim LockAttempts { get; } = new(0);
        public SemaphoreSlim DeleteStarted { get; } = new(0);
        public int LocksReleased { get; private set; }

        public Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
        {
            LockAttempts.Release();
            if (LockFailure is not null)
                throw LockFailure;
            return Task.FromResult<IAsyncDisposable?>(LockAvailable ? new Lease(this) : null);
        }

        public async Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken cancellationToken)
        {
            Calls.Add(pass);
            BatchSizes.Add(batchSize);
            DeleteStarted.Release();
            if (DeleteFailure is not null)
                throw DeleteFailure;
            if (BlockDeletes)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException) when (CancelAs is not null)
                {
                    throw CancelAs;
                }
            }
            return Results.TryGetValue(pass, out var queue) && queue.Count > 0 ? queue.Dequeue() : 0;
        }

        private sealed class Lease(FakeStore store) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                store.LocksReleased++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
