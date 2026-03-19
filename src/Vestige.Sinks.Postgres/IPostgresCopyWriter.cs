namespace Vestige.Sinks.Postgres;

internal interface IPostgresCopyWriter : IAsyncDisposable
{
    Task InitializeAsync(PostgresSinkOptions options, CancellationToken cancellationToken);

    Task EnsureStorageAsync(CancellationToken cancellationToken);

    Task WriteSingleAsync(PostgresEventRow row, CancellationToken cancellationToken);

    Task WriteAsync(IReadOnlyList<PostgresEventRow> batch, CancellationToken cancellationToken);
}
