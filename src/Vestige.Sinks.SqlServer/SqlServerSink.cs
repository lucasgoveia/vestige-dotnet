using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vestige.Retention;

namespace Vestige.Sinks.SqlServer;

/// <summary>Persists wide events to SQL Server using inserts for single rows and bulk copy for batches.</summary>
public sealed class SqlServerSink : IWideEventSink
{
    private readonly SqlServerSinkOptions _options;
    private readonly ILogger _logger;
    private readonly ISqlServerWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Func<SqlServerTableLayout, IReadOnlyList<PromotedField>, CancellationToken, Task>? _buildIndexes;
    private readonly CancellationTokenSource _backgroundCts = new();
    private Task _indexBuild = Task.CompletedTask;
    private readonly Func<SqlServerTableLayout, IRetentionStore>? _retentionStore;
    private RetentionLoop? _retention;
    private Task _retentionRun = Task.CompletedTask;
    private SqlServerTableLayout? _layout;
    private PromotionWarningLimiter? _warnings;
    private int _initialized;
    private int _disposed;

    public SqlServerSink(IOptions<SqlServerSinkOptions> options)
        : this(options, NullLogger<SqlServerSink>.Instance)
    {
    }

    public SqlServerSink(IOptions<SqlServerSinkOptions> options, ILogger<SqlServerSink> logger)
        : this(options, logger, new SqlServerWriter(), TimeProvider.System)
    {
    }

    internal SqlServerSink(
        IOptions<SqlServerSinkOptions> options,
        ILogger logger,
        ISqlServerWriter writer,
        TimeProvider timeProvider,
        Func<SqlServerTableLayout, IReadOnlyList<PromotedField>, CancellationToken, Task>? buildIndexes = null,
        Func<SqlServerTableLayout, IRetentionStore>? retentionStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options.Value;
        _logger = logger;
        _writer = writer;
        _timeProvider = timeProvider;
        _buildIndexes = buildIndexes;
        _retentionStore = retentionStore;
    }

    /// <summary>Completes when background promoted-index builds started by <see cref="InitializeAsync"/> finish.</summary>
    internal Task IndexBuildCompletion => _indexBuild;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        SqlServerSinkOptionsValidator.Validate(_options);
        var layout = new SqlServerTableLayout(_options);
        _warnings = new PromotionWarningLimiter(_logger, _timeProvider);

        await _writer.InitializeAsync(_options, layout, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<PromotedField> pendingIndexes = [];
        if (_options.AutoCreate)
            pendingIndexes = await _writer.EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        else
            await _writer.VerifyStorageAsync(cancellationToken).ConfigureAwait(false);

        _layout = layout;

        if (pendingIndexes.Count > 0)
            _indexBuild = RunBackgroundAsync(token => BuildIndexesAsync(layout, pendingIndexes, token));

        if (_options.Retention.Enabled)
        {
            var store = _retentionStore?.Invoke(layout) ?? new SqlServerRetentionStore(_options.ConnectionString, layout);
            _retention = new RetentionLoop(RetentionPlanner.Plan(_options.Retention), store, _logger, _timeProvider);
            _retentionRun = RunBackgroundAsync(_retention.RunAsync);
        }
    }

    /// <summary>Run one retention cycle immediately (tests and operational tooling).</summary>
    internal Task<RetentionCycleResult> RunRetentionCycleAsync(CancellationToken cancellationToken)
        => _retention is { } retention
            ? retention.RunCycleAsync(cancellationToken)
            : throw new InvalidOperationException("Retention is not enabled for this sink.");

    public Task EmitAsync(WideEventData data, CancellationToken cancellationToken = default)
        => WriteAsync([MapRow(data)], cancellationToken);

    public async Task EmitBatchAsync(IReadOnlyList<WideEventData> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
            return;

        var rows = new List<SqlServerEventRow>(batch.Count);
        foreach (var data in batch)
            rows.Add(MapRow(data));

        await WriteAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => WaitForInFlightWriteAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _backgroundCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_indexBuild, _retentionRun).ConfigureAwait(false);

        await WaitForInFlightWriteAsync(CancellationToken.None).ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _backgroundCts.Dispose();
    }

    private Task BuildIndexesAsync(SqlServerTableLayout layout, IReadOnlyList<PromotedField> pending, CancellationToken cancellationToken)
        => _buildIndexes is not null
            ? _buildIndexes(layout, pending, cancellationToken)
            : new SqlServerIndexBuilder(_options.ConnectionString, layout, _logger).BuildAsync(pending, cancellationToken);

    private Task RunBackgroundAsync(Func<CancellationToken, Task> work)
    {
        var token = _backgroundCts.Token;
        return Task.Run(async () =>
        {
            try
            {
                await work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vestige SQL Server sink background work failed.");
            }
        }, CancellationToken.None);
    }

    private SqlServerEventRow MapRow(WideEventData data)
    {
        var layout = _layout ?? throw new InvalidOperationException("SQL Server sink must be initialized before emitting events.");
        return SqlServerEventRow.Map(data, layout.Promoted, _warnings);
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
