using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Vestige;
using Vestige.Internal;
using Vestige.Sinks.Postgres;

namespace Vestige.Tests;

public sealed class PostgresSinkTests
{
    private static WideEventData MakeData(Action<WideEvent>? configure = null)
    {
        var ev = new WideEvent
        {
            ServiceName = "checkout-service",
            TraceId = "trace-123",
            SpanId = "span-456",
            DurationMs = 42.5,
            Outcome = "success",
            StatusCode = 200,
        };

        ev.Set("service.version", "1.2.3");
        ev.Set("deployment.environment", "production");
        ev.Set("cloud.region", "us-east-1");
        ev.Set("http.method", "POST");
        ev.Set("http.path", "/orders");
        ev.Set("customer.id", "cus_123");
        ev.Set("timer.db.insert", 17L);

        configure?.Invoke(ev);

        return new SystemTextJsonSerializer().Serialize(ev);
    }

    [Fact]
    public void Map_ExtractsPromotedColumns_AndPreservesPayload()
    {
        var row = PostgresEventRow.Map(MakeData());

        Assert.Equal("checkout-service", row.ServiceName);
        Assert.Equal("1.2.3", row.ServiceVersion);
        Assert.Equal("production", row.DeploymentEnvironment);
        Assert.Equal("us-east-1", row.CloudRegion);
        Assert.Equal("trace-123", row.TraceId);
        Assert.Equal("span-456", row.SpanId);
        Assert.Equal("success", row.Outcome);
        Assert.Equal(42.5, row.DurationMs);
        Assert.Equal(200, row.HttpStatusCode);
        Assert.Equal("POST", row.HttpMethod);
        Assert.Equal("/orders", row.HttpPath);

        using var doc = JsonDocument.Parse(row.PayloadUtf8);
        Assert.Equal("cus_123", doc.RootElement.GetProperty("customer.id").GetString());
        Assert.Equal(17L, doc.RootElement.GetProperty("timer.db.insert").GetInt64());
    }

    [Fact]
    public void Map_WhenEventIdExceeds256_Throws()
    {
        var data = new WideEventData
        {
            Fields = new Dictionary<string, object?>
            {
                ["event_id"] = new string('x', 257),
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["duration_ms"] = 12.3,
            },
            JsonBytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>()),
            OriginalEvent = new WideEvent(),
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PostgresEventRow.Map(data));
        Assert.Equal("eventId", ex.ParamName);
    }

    [Fact]
    public async Task EmitBatchAsync_WithMultipleItems_WritesImmediatelyUsingCopy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var writer = new FakeCopyWriter();
        var sink = new PostgresSink(
            Options.Create(new PostgresSinkOptions
            {
                ConnectionString = "Host=localhost;Database=vestige;Username=test;Password=test",
                AutoCreate = false,
            }),
            writer);

        await sink.InitializeAsync(cancellationToken);
        await sink.EmitBatchAsync([MakeData(), MakeData(ev => ev.Outcome = "error")], cancellationToken);

        var batch = Assert.Single(writer.Batches);
        Assert.Equal(2, batch.Count);
        Assert.Empty(writer.Inserts);
    }

    [Fact]
    public async Task EmitAsync_UsesInsertWriter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var writer = new FakeCopyWriter();
        var sink = new PostgresSink(
            Options.Create(new PostgresSinkOptions
            {
                ConnectionString = "Host=localhost;Database=vestige;Username=test;Password=test",
                AutoCreate = false,
            }),
            writer);

        await sink.InitializeAsync(cancellationToken);
        await sink.EmitAsync(MakeData(), cancellationToken);

        var insert = Assert.Single(writer.Inserts);
        Assert.Equal("checkout-service", insert.ServiceName);
        Assert.Empty(writer.Batches);
    }

    [Fact]
    public async Task EmitBatchAsync_WithSingleItem_UsesInsertWriter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var writer = new FakeCopyWriter();
        var sink = new PostgresSink(
            Options.Create(new PostgresSinkOptions
            {
                ConnectionString = "Host=localhost;Database=vestige;Username=test;Password=test",
                AutoCreate = false,
            }),
            writer);

        await sink.InitializeAsync(cancellationToken);
        await sink.EmitBatchAsync([MakeData()], cancellationToken);

        Assert.Single(writer.Inserts);
        Assert.Empty(writer.Batches);
    }

    [Fact]
    public async Task FlushAsync_WaitsForInFlightWrite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var writer = new BlockingCopyWriter();
        var sink = new PostgresSink(
            Options.Create(new PostgresSinkOptions
            {
                ConnectionString = "Host=localhost;Database=vestige;Username=test;Password=test",
                AutoCreate = false,
            }),
            writer);

        await sink.InitializeAsync(cancellationToken);
        var emitTask = sink.EmitBatchAsync([MakeData(), MakeData(ev => ev.Outcome = "error")], cancellationToken);

        await writer.Started.Task.WaitAsync(cancellationToken);

        var flushTask = sink.FlushAsync(cancellationToken);
        Assert.False(flushTask.IsCompleted);

        writer.AllowCompletion.SetResult();
        await emitTask;
        await flushTask;
    }

    [Fact]
    public async Task DisposeAsync_DisposesWriter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var writer = new FakeCopyWriter();
        var sink = new PostgresSink(
            Options.Create(new PostgresSinkOptions
            {
                ConnectionString = "Host=localhost;Database=vestige;Username=test;Password=test",
                AutoCreate = false,
            }),
            writer);

        await sink.InitializeAsync(cancellationToken);
        await sink.EmitAsync(MakeData(), cancellationToken);
        await sink.DisposeAsync();

        Assert.True(writer.Disposed);
    }

    [Fact]
    public void AddPostgresSink_RegistersSinkAndOptions()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        services
            .AddVestige()
            .AddPostgresSink(options =>
            {
                options.ConnectionString = "Host=localhost;Database=vestige;Username=test;Password=test";
                options.Table = "events";
            });

        var sinkDescriptor = Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IWideEventSink) &&
                descriptor.ImplementationType == typeof(PostgresSink));

        Assert.Equal(typeof(PostgresSink), sinkDescriptor.ImplementationType);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IConfigureOptions<PostgresSinkOptions>));
    }

    private sealed class FakeCopyWriter : IPostgresCopyWriter
    {
        private readonly ConcurrentQueue<PostgresEventRow> _inserts = new();
        private readonly ConcurrentQueue<IReadOnlyList<PostgresEventRow>> _batches = new();

        public IReadOnlyCollection<PostgresEventRow> Inserts => _inserts.ToArray();
        public IReadOnlyCollection<IReadOnlyList<PostgresEventRow>> Batches => _batches.ToArray();
        public bool Disposed { get; private set; }

        public Task InitializeAsync(PostgresSinkOptions options, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task EnsureStorageAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task WriteSingleAsync(PostgresEventRow row, CancellationToken cancellationToken)
        {
            _inserts.Enqueue(row);
            return Task.CompletedTask;
        }

        public Task WriteAsync(
            IReadOnlyList<PostgresEventRow> batch,
            CancellationToken cancellationToken)
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

    private sealed class BlockingCopyWriter : IPostgresCopyWriter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(PostgresSinkOptions options, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task EnsureStorageAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task WriteSingleAsync(PostgresEventRow row, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public async Task WriteAsync(IReadOnlyList<PostgresEventRow> batch, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await AllowCompletion.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
