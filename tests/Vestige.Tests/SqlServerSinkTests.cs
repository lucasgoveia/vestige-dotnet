using Microsoft.Extensions.Options;
using Vestige.Internal;
using Vestige.Retention;
using Vestige.Sinks.SqlServer;
using Vestige.Tests.TestSupport;

namespace Vestige.Tests;

public sealed class SqlServerSinkTests
{
    private static WideEventData MakeData(Action<WideEvent>? configure = null)
    {
        var ev = new WideEvent { ServiceName = "client-manager", DurationMs = 3, Outcome = "success" };
        configure?.Invoke(ev);
        return new SystemTextJsonSerializer().Serialize(ev);
    }

    private static SqlServerSink CreateSink(
        FakeSqlServerWriter writer,
        Action<SqlServerSinkOptions>? configure = null,
        ListLogger<SqlServerSink>? logger = null)
    {
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        configure?.Invoke(options);
        return new SqlServerSink(Options.Create(options), logger ?? new ListLogger<SqlServerSink>(), writer, TimeProvider.System);
    }

    [Fact]
    public async Task Initialize_PassesLayoutAndEnsuresStorage()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("audit_action", Assert.Single(writer.Layout!.Promoted).Column);
        Assert.Equal(1, writer.EnsureCalls);
        Assert.Equal(0, writer.VerifyCalls);
    }

    [Fact]
    public async Task Initialize_AutoCreateDisabled_VerifiesInsteadOfEnsuring()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.AutoCreate = false);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, writer.EnsureCalls);
        Assert.Equal(1, writer.VerifyCalls);
    }

    [Fact]
    public async Task Initialize_InvalidPromotedFields_Throws()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.PromotedFields.Add("http.method", PromotedType.String(16)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Emit_SingleAndBatch_CarryPromotedValues()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await sink.EmitAsync(MakeData(ev => ev.Set("audit_action", "tenant.create")), TestContext.Current.CancellationToken);
        await sink.EmitBatchAsync(
            [MakeData(ev => ev.Set("audit_action", "api_key.revoke")), MakeData()],
            TestContext.Current.CancellationToken);

        Assert.Equal("tenant.create", Assert.Single(writer.Inserts).PromotedValues[0]);
        var batch = Assert.Single(writer.Batches);
        Assert.Equal("api_key.revoke", batch[0].PromotedValues[0]);
        Assert.Null(batch[1].PromotedValues[0]);
    }

    [Fact]
    public async Task Emit_BeforeInitialize_Throws()
    {
        await using var sink = CreateSink(new FakeSqlServerWriter());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.EmitAsync(MakeData(), TestContext.Current.CancellationToken));
        Assert.Contains("must be initialized", ex.Message);
    }

    [Fact]
    public async Task Initialize_ReturnsBeforePendingIndexBuildFinishes()
    {
        var writer = new FakeSqlServerWriter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<PromotedField>? built = null;
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        writer.PendingIndexes = [options.PromotedFields[0]];
        await using var sink = new SqlServerSink(
            Options.Create(options), new ListLogger<SqlServerSink>(), writer, TimeProvider.System,
            buildIndexes: async (_, pending, ct) =>
            {
                built = pending;
                started.SetResult();
                await release.Task.WaitAsync(ct);
            });

        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(sink.IndexBuildCompletion.IsCompleted);
        Assert.Equal("audit_action", Assert.Single(built!).Column);
        release.SetResult();
        await sink.IndexBuildCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Initialize_NothingPending_DoesNotStartBuilder()
    {
        var calls = 0;
        await using var sink = new SqlServerSink(
            Options.Create(new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" }),
            new ListLogger<SqlServerSink>(), new FakeSqlServerWriter(), TimeProvider.System,
            buildIndexes: (_, _, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(sink.IndexBuildCompletion.IsCompletedSuccessfully);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Dispose_CancelsRunningIndexBuild_WithoutThrowing()
    {
        var writer = new FakeSqlServerWriter();
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        writer.PendingIndexes = [options.PromotedFields[0]];
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new SqlServerSink(
            Options.Create(options), new ListLogger<SqlServerSink>(), writer, TimeProvider.System,
            buildIndexes: async (_, _, ct) =>
            {
                ct.Register(() => cancelled.TrySetResult());
                await Task.Delay(Timeout.Infinite, ct);
            });
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await sink.DisposeAsync();

        Assert.True(cancelled.Task.IsCompleted);
        Assert.True(writer.Disposed);
    }

    [Fact]
    public async Task RetentionCycle_UsesPlannedPassesAgainstStore()
    {
        var store = new RecordingRetentionStore();
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        options.Retention.Default = TimeSpan.FromDays(30);
        options.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));
        await using var sink = new SqlServerSink(
            Options.Create(options), new ListLogger<SqlServerSink>(), new FakeSqlServerWriter(), TimeProvider.System,
            retentionStore: _ => store);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        var result = await sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Acquired);
        Assert.Equal(new[] { TimeSpan.FromDays(30), TimeSpan.FromDays(365) }, store.Passes.Select(p => p.Period));
    }

    [Fact]
    public async Task RetentionCycle_WhenRetentionOff_Throws()
    {
        await using var sink = CreateSink(new FakeSqlServerWriter());
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Initialize_TransientStorageFailure_LaterEmitStillWrites()
    {
        var writer = new FakeSqlServerWriter { EnsureFailure = new TimeoutException("database resuming") };
        await using var sink = CreateSink(writer);

        await Assert.ThrowsAsync<TimeoutException>(() => sink.InitializeAsync(TestContext.Current.CancellationToken));
        await sink.EmitAsync(MakeData(), TestContext.Current.CancellationToken);

        Assert.Single(writer.Inserts);
    }

    [Fact]
    public async Task Initialize_SchemaError_EmitFailsWithOriginalCause()
    {
        var writer = new FakeSqlServerWriter
        {
            EnsureFailure = new InvalidOperationException("Promoted column [tenant_id] exists but is NOT NULL."),
        };
        await using var sink = CreateSink(writer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.InitializeAsync(TestContext.Current.CancellationToken));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.EmitAsync(MakeData(), TestContext.Current.CancellationToken));

        Assert.Contains("NOT NULL", ex.Message);
        Assert.Empty(writer.Inserts);
    }

    [Fact]
    public async Task Dispose_BackgroundWorkFaultingWithNonCancellationException_IsNotLoggedAsError()
    {
        var writer = new FakeSqlServerWriter();
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        writer.PendingIndexes = [options.PromotedFields[0]];
        var logger = new ListLogger<SqlServerSink>();
        var sink = new SqlServerSink(
            Options.Create(options), logger, writer, TimeProvider.System,
            buildIndexes: async (_, _, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    // Microsoft.Data.SqlClient surfaces a cancelled command as a SqlException, not an OCE.
                    throw new InvalidOperationException("Operation cancelled by user.");
                }
            });
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await sink.DisposeAsync();

        Assert.DoesNotContain(logger.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task Dispose_BackgroundWorkIgnoringCancellation_CompletesWithinBound()
    {
        var writer = new FakeSqlServerWriter();
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        options.PromotedFields.Add("audit_action", PromotedType.String(128));
        writer.PendingIndexes = [options.PromotedFields[0]];
        var sink = new SqlServerSink(
            Options.Create(options), new ListLogger<SqlServerSink>(), writer, TimeProvider.System,
            buildIndexes: (_, _, _) => Task.Delay(Timeout.Infinite, CancellationToken.None));
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await sink.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.True(writer.Disposed);
    }

    private sealed class RecordingRetentionStore : IRetentionStore
    {
        public List<RetentionPass> Passes { get; } = [];

        public Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
            => Task.FromResult<IAsyncDisposable?>(new NoopLease());

        public Task<int> DeleteBatchAsync(RetentionPass pass, int batchSize, CancellationToken cancellationToken)
        {
            Passes.Add(pass);
            return Task.FromResult(0);
        }

        private sealed class NoopLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
