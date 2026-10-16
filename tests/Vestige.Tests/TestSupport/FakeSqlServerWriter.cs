using System.Collections.Concurrent;
using Vestige.Sinks.SqlServer;

namespace Vestige.Tests.TestSupport;

internal sealed class FakeSqlServerWriter : ISqlServerWriter
{
    private readonly ConcurrentQueue<SqlServerEventRow> _inserts = new();
    private readonly ConcurrentQueue<IReadOnlyList<SqlServerEventRow>> _batches = new();

    public SqlServerTableLayout? Layout { get; private set; }
    public IReadOnlyList<PromotedField> PendingIndexes { get; set; } = [];
    public Exception? EnsureFailure { get; set; }
    public int EnsureCalls { get; private set; }
    public int VerifyCalls { get; private set; }
    public bool Disposed { get; private set; }
    public IReadOnlyCollection<SqlServerEventRow> Inserts => _inserts.ToArray();
    public IReadOnlyCollection<IReadOnlyList<SqlServerEventRow>> Batches => _batches.ToArray();

    public Task InitializeAsync(SqlServerSinkOptions options, SqlServerTableLayout layout, CancellationToken cancellationToken)
    {
        Layout = layout;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PromotedField>> EnsureStorageAsync(CancellationToken cancellationToken)
    {
        EnsureCalls++;
        if (EnsureFailure is not null)
            return Task.FromException<IReadOnlyList<PromotedField>>(EnsureFailure);
        return Task.FromResult(PendingIndexes);
    }

    public Task VerifyStorageAsync(CancellationToken cancellationToken)
    {
        VerifyCalls++;
        return Task.CompletedTask;
    }

    public Task WriteSingleAsync(SqlServerEventRow row, CancellationToken cancellationToken)
    {
        _inserts.Enqueue(row);
        return Task.CompletedTask;
    }

    public Task WriteAsync(IReadOnlyList<SqlServerEventRow> batch, CancellationToken cancellationToken)
    {
        _batches.Enqueue(batch.ToArray());
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
