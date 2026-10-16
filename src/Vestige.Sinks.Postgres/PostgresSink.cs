using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vestige.Retention;

namespace Vestige.Sinks.Postgres;

/// <summary>Persists wide events to PostgreSQL using inserts for single rows and COPY for batches.</summary>
public sealed class PostgresSink : IWideEventSink
{
    private readonly PostgresSinkOptions _options;
    private readonly IPostgresCopyWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _backgroundCts = new();
    private RetentionLoop? _retention;
    private Task _retentionRun = Task.CompletedTask;
    private int _initialized;
    private int _disposed;

    public PostgresSink(IOptions<PostgresSinkOptions> options)
        : this(options, NullLogger<PostgresSink>.Instance)
    {
    }

    public PostgresSink(IOptions<PostgresSinkOptions> options, ILogger<PostgresSink> logger)
        : this(options, new NpgsqlPostgresCopyWriter(), logger, TimeProvider.System)
    {
    }

    internal PostgresSink(IOptions<PostgresSinkOptions> options, IPostgresCopyWriter writer)
        : this(options, writer, NullLogger<PostgresSink>.Instance, TimeProvider.System)
    {
    }

    internal PostgresSink(
        IOptions<PostgresSinkOptions> options,
        IPostgresCopyWriter writer,
        ILogger logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options.Value;
        _writer = writer;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        var passes = RetentionPlanner.Plan(_options.Retention);

        await _writer.InitializeAsync(_options, cancellationToken).ConfigureAwait(false);

        if (_options.AutoCreate)
            await _writer.EnsureStorageAsync(cancellationToken).ConfigureAwait(false);

        if (passes.Count > 0)
        {
            _retention = new RetentionLoop(
                passes,
                new PostgresRetentionStore(_options.ConnectionString, _options),
                _logger,
                _timeProvider);
            var token = _backgroundCts.Token;
            _retentionRun = Task.Run(() => _retention.RunAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Run one retention cycle immediately (tests and operational tooling).</summary>
    internal Task<RetentionCycleResult> RunRetentionCycleAsync(CancellationToken cancellationToken)
        => _retention is { } retention
            ? retention.RunCycleAsync(cancellationToken)
            : throw new InvalidOperationException("Retention is not enabled for this sink.");

    public Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default)
        => WriteAsync([PostgresEventRow.Map(data)], cancellationToken);

    public async Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
            return;

        var rows = new List<PostgresEventRow>(batch.Count);
        foreach (var data in batch)
            rows.Add(PostgresEventRow.Map(data));

        await WriteAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => WaitForInFlightWriteAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _backgroundCts.CancelAsync().ConfigureAwait(false);
        await _retentionRun.ConfigureAwait(false);

        await WaitForInFlightWriteAsync(CancellationToken.None).ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _backgroundCts.Dispose();
    }

    private async Task WriteAsync(IReadOnlyList<PostgresEventRow> rows, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (Volatile.Read(ref _initialized) == 0)
            throw new InvalidOperationException("Postgres sink must be initialized before emitting events.");

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
