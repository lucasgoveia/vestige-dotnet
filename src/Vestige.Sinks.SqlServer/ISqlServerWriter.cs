namespace Vestige.Sinks.SqlServer;

internal interface ISqlServerWriter : IAsyncDisposable
{
    Task InitializeAsync(SqlServerSinkOptions options, SqlServerTableLayout layout, CancellationToken cancellationToken);

    /// <summary>Create or upgrade storage; returns promoted indexes that still need a background build.</summary>
    Task<IReadOnlyList<PromotedField>> EnsureStorageAsync(CancellationToken cancellationToken);

    /// <summary>With AutoCreate disabled: fail when promoted columns are missing or incompatible.</summary>
    Task VerifyStorageAsync(CancellationToken cancellationToken);

    Task WriteSingleAsync(SqlServerEventRow row, CancellationToken cancellationToken);

    Task WriteAsync(IReadOnlyList<SqlServerEventRow> batch, CancellationToken cancellationToken);
}
