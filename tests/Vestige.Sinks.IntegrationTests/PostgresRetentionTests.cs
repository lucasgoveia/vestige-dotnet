using Microsoft.Extensions.Options;
using Npgsql;
using Vestige.Sinks.IntegrationTests.Support;
using Vestige.Sinks.Postgres;

namespace Vestige.Sinks.IntegrationTests;

public sealed class PostgresRetentionTests(PostgresFixture db)
{
    private PostgresSink CreateSink(string table) => new(
        Options.Create(CreateOptions(table)),
        new ListLogger<PostgresSink>());

    private PostgresSinkOptions CreateOptions(string table)
    {
        var options = new PostgresSinkOptions { ConnectionString = db.ConnectionString, Schema = "vestige", Table = table };
        options.Retention.Default = TimeSpan.FromDays(30);
        options.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));
        return options;
    }

    private async Task SeedAsync(PostgresSink sink, string table)
    {
        await sink.EmitBatchAsync(
        [
            TestEvents.Make(ev => ev.Set("seed", "telemetry_old")),
            TestEvents.Make(ev => ev.Set("seed", "telemetry_new")),
            TestEvents.Make(ev => { ev.Set("seed", "audit_mid"); ev.Set("audit_action", "tenant.create"); }),
            TestEvents.Make(ev => { ev.Set("seed", "audit_old"); ev.Set("audit_action", "tenant.remove"); }),
        ], TestContext.Current.CancellationToken);

        await ExecuteAsync($"""
            UPDATE vestige."{table}" SET timestamp_utc = now() - interval '40 days' WHERE payload->>'seed' = 'telemetry_old';
            UPDATE vestige."{table}" SET timestamp_utc = now() - interval '1 day' WHERE payload->>'seed' = 'telemetry_new';
            UPDATE vestige."{table}" SET timestamp_utc = now() - interval '100 days' WHERE payload->>'seed' = 'audit_mid';
            UPDATE vestige."{table}" SET timestamp_utc = now() - interval '400 days' WHERE payload->>'seed' = 'audit_old';
            """);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> RemainingSeedsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT string_agg(payload->>'seed', ',' ORDER BY payload->>'seed') FROM vestige.\"{table}\"", connection);
        return (string?)await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Cycle_KeepsAuditLongerThanTelemetry()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = CreateSink(table);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedAsync(sink, table);

        var result = await sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Acquired);
        Assert.Equal(new[] { 1L, 1L }, result.DeletedPerPass);
        Assert.Equal("audit_mid,telemetry_new", await RemainingSeedsAsync(table));
    }

    [Fact]
    public async Task Cycle_AdvisoryLockHeldElsewhere_Skips()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        await using var sink = CreateSink(table);
        await sink.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedAsync(sink, table);
        await using var holder = new NpgsqlConnection(db.ConnectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(@ns, hashtext(@key))", holder))
        {
            lockCommand.Parameters.AddWithValue("ns", PostgresRetentionSql.LockNamespace);
            lockCommand.Parameters.AddWithValue("key", PostgresRetentionSql.LockKey("vestige", table));
            await lockCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var result = await sink.RunRetentionCycleAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Acquired);
        Assert.Equal("audit_mid,audit_old,telemetry_new,telemetry_old", await RemainingSeedsAsync(table));
    }

    [Fact]
    public async Task Initialize_InvalidRetention_Throws()
    {
        db.SkipIfUnavailable();
        var options = new PostgresSinkOptions { ConnectionString = db.ConnectionString, Table = SqlTestDb.UniqueTable() };
        options.Retention.KeepLonger("audit_action", TimeSpan.FromDays(365));
        await using var sink = new PostgresSink(Options.Create(options), new ListLogger<PostgresSink>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.InitializeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lease_HoldsNoSessionAdvisoryLock_SoTransactionPoolersCannotLeakIt()
    {
        db.SkipIfUnavailable();
        var table = SqlTestDb.UniqueTable();
        var store = new PostgresRetentionStore(db.ConnectionString, CreateOptions(table));

        await using var lease = await store.TryAcquireLockAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(lease);
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted", connection);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }
}
