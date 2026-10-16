using Microsoft.Extensions.Options;
using Vestige.Internal;
using Vestige.Sinks.SqlServer;
using Vestige.Tests.TestSupport;

namespace Vestige.Tests;

public sealed class SqlServerSinkTests
{
    private static WideEventData MakeData(Action<WideEvent>? configure = null)
    {
        var ev = new WideEvent { ServiceName = "client-manager", DurationMs = 3, Outcome = "success" };
        configure?.Invoke(ev);
        return new SystemTextJsonSerializer().Serialize(ev);
    }

    private static SqlServerSink CreateSink(
        FakeSqlServerWriter writer,
        Action<SqlServerSinkOptions>? configure = null,
        ListLogger<SqlServerSink>? logger = null)
    {
        var options = new SqlServerSinkOptions { ConnectionString = "Server=.;Database=x" };
        configure?.Invoke(options);
        return new SqlServerSink(Options.Create(options), logger ?? new ListLogger<SqlServerSink>(), writer, TimeProvider.System);
    }

    [Fact]
    public async Task Initialize_PassesLayoutAndEnsuresStorage()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("audit_action", Assert.Single(writer.Layout!.Promoted).Column);
        Assert.Equal(1, writer.EnsureCalls);
        Assert.Equal(0, writer.VerifyCalls);
    }

    [Fact]
    public async Task Initialize_AutoCreateDisabled_VerifiesInsteadOfEnsuring()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.AutoCreate = false);

        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, writer.EnsureCalls);
        Assert.Equal(1, writer.VerifyCalls);
    }

    [Fact]
    public async Task Initialize_InvalidPromotedFields_Throws()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.PromotedFields.Add("http.method", PromotedType.String(16)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InitializeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Emit_SingleAndBatch_CarryPromotedValues()
    {
        var writer = new FakeSqlServerWriter();
        await using var sink = CreateSink(writer, o => o.PromotedFields.Add("audit_action", PromotedType.String(128)));
        await sink.InitializeAsync(TestContext.Current.CancellationToken);

        await sink.EmitAsync(MakeData(ev => ev.Set("audit_action", "tenant.create")), TestContext.Current.CancellationToken);
        await sink.EmitBatchAsync(
            [MakeData(ev => ev.Set("audit_action", "api_key.revoke")), MakeData()],
            TestContext.Current.CancellationToken);

        Assert.Equal("tenant.create", Assert.Single(writer.Inserts).PromotedValues[0]);
        var batch = Assert.Single(writer.Batches);
        Assert.Equal("api_key.revoke", batch[0].PromotedValues[0]);
        Assert.Null(batch[1].PromotedValues[0]);
    }

    [Fact]
    public async Task Emit_BeforeInitialize_Throws()
    {
        await using var sink = CreateSink(new FakeSqlServerWriter());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.EmitAsync(MakeData(), TestContext.Current.CancellationToken));
        Assert.Contains("must be initialized", ex.Message);
    }
}
