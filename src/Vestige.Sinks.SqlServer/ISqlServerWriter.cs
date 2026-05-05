namespace Vestige.Sinks.SqlServer;

internal interface ISqlServerWriter : IAsyncDisposable
{
    Task InitializeAsync(SqlServerSinkOptions options, CancellationToken cancellationToken);

    Task EnsureStorageAsync(CancellationToken cancellationToken);

    Task WriteSingleAsync(SqlServerEventRow row, CancellationToken cancellationToken);

    Task WriteAsync(IReadOnlyList<SqlServerEventRow> batch, CancellationToken cancellationToken);
}
