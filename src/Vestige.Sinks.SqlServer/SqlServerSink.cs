using Microsoft.Extensions.Options;

namespace Vestige.Sinks.SqlServer;

/// <summary>Persists wide events to SQL Server using inserts for single rows and bulk copy for batches.</summary>
public sealed class SqlServerSink : IWideEventSink
{
    private readonly SqlServerSinkOptions _options;
    private readonly ISqlServerWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _initialized;
    private int _disposed;

    public SqlServerSink(IOptions<SqlServerSinkOptions> options)
        : this(options, new SqlServerWriter())
    {
    }

    internal SqlServerSink(IOptions<SqlServerSinkOptions> options, ISqlServerWriter writer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);

        _options = options.Value;
        _writer = writer;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        await _writer.InitializeAsync(_options, cancellationToken).ConfigureAwait(false);

        if (_options.AutoCreate)
            await _writer.EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default)
        => WriteAsync([SqlServerEventRow.Map(data)], cancellationToken);

    public async Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
            return;

        var rows = new List<SqlServerEventRow>(batch.Count);
        foreach (var data in batch)
            rows.Add(SqlServerEventRow.Map(data));

        await WriteAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => WaitForInFlightWriteAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await WaitForInFlightWriteAsync(CancellationToken.None).ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private async Task WriteAsync(IReadOnlyList<SqlServerEventRow> rows, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (Volatile.Read(ref _initialized) == 0)
            throw new InvalidOperationException("SQL Server sink must be initialized before emitting events.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (rows.Count == 1)
                await _writer.WriteSingleAsync(rows[0], cancellationToken).ConfigureAwait(false);
            else
                await _writer.WriteAsync(rows, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WaitForInFlightWriteAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _writeLock.Release();
    }
}
