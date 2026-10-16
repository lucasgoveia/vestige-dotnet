using Testcontainers.PostgreSql;

namespace Vestige.Sinks.IntegrationTests.Support;

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _unavailableReason;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("VESTIGE_REQUIRE_DOCKER") != "1")
        {
            _unavailableReason = $"Postgres container unavailable: {ex.Message}";
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
