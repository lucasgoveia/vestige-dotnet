using Testcontainers.MsSql;

namespace Vestige.Sinks.IntegrationTests.Support;

/// <summary>
/// One SQL Server 2022 Developer container (EngineEdition 3) per test run. Tests are skipped when
/// Docker is unavailable, unless VESTIGE_REQUIRE_DOCKER=1 (CI), in which case startup fails.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private string? _unavailableReason;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("VESTIGE_REQUIRE_DOCKER") != "1")
        {
            _unavailableReason = $"SQL Server container unavailable: {ex.Message}";
        }
    }

    public void SkipIfUnavailable()
    {
        if (_unavailableReason is not null)
            Assert.Skip(_unavailableReason);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
