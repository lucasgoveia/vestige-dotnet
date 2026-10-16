using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Vestige.Internal;
using Vestige.Sinks.SqlServer;
using Vestige.Tests.TestSupport;

namespace Vestige.Tests;

public sealed class SqlServerEventRowTests
{
    private static readonly Guid TenantId = Guid.Parse("6f1c2a5e-1d2b-4c3d-9e8f-0a1b2c3d4e5f");

    private static readonly PromotedField[] Promoted =
    [
        new("audit_action", PromotedType.String(128), "audit_action", true),
        new("tenant_id", PromotedType.Guid, "tenant_id", true),
        new("actor_user_id", PromotedType.Guid, "actor_user_id", true),
    ];

    private static WideEventData MakeData(Action<WideEvent> configure)
    {
        var ev = new WideEvent { ServiceName = "client-manager", DurationMs = 5, Outcome = "success" };
        configure(ev);
        return new SystemTextJsonSerializer().Serialize(ev);
    }

    [Fact]
    public void Map_WithoutPromotedFields_HasEmptyPromotedValues()
    {
        var row = SqlServerEventRow.Map(MakeData(_ => { }));

        Assert.Empty(row.PromotedValues);
    }

    [Fact]
    public void Map_PromotedFields_ConvertedInDeclaredOrder()
    {
        var data = MakeData(ev =>
        {
            ev.Set("tenant_id", TenantId);
            ev.Set("audit_action", "tenant.create");
            ev.Set("actor_user_id", TenantId.ToString());
        });

        var row = SqlServerEventRow.Map(data, Promoted, warnings: null);

        Assert.Equal(new object?[] { "tenant.create", TenantId, TenantId }, row.PromotedValues);
    }

    [Fact]
    public void Map_AbsentOrExplicitNullField_WritesNullWithoutWarning()
    {
        var logger = new ListLogger<SqlServerSink>();
        var warnings = new PromotionWarningLimiter(logger, new FakeTimeProvider());
        var data = MakeData(ev => ev.Set("tenant_id", null));

        var row = SqlServerEventRow.Map(data, Promoted, warnings);

        Assert.Equal(new object?[] { null, null, null }, row.PromotedValues);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Map_UnconvertibleValue_WritesNullAndWarnsOncePerMinuteWithoutLeakingValue()
    {
        var logger = new ListLogger<SqlServerSink>();
        var time = new FakeTimeProvider();
        var warnings = new PromotionWarningLimiter(logger, time);
        var data = MakeData(ev => ev.Set("tenant_id", "secret-not-a-guid"));

        var first = SqlServerEventRow.Map(data, Promoted, warnings);
        SqlServerEventRow.Map(data, Promoted, warnings);
        time.Advance(TimeSpan.FromSeconds(61));
        SqlServerEventRow.Map(data, Promoted, warnings);

        Assert.Null(first.PromotedValues[1]);
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.All(logger.Entries, e =>
        {
            Assert.Contains("tenant_id", e.Message);
            Assert.DoesNotContain("secret-not-a-guid", e.Message);
        });
    }

    [Fact]
    public void Map_UnconvertibleValue_KeepsOriginalInPayload()
    {
        var data = MakeData(ev => ev.Set("tenant_id", "not-a-guid"));

        var row = SqlServerEventRow.Map(data, Promoted, warnings: null);

        using var payload = System.Text.Json.JsonDocument.Parse(row.PayloadJson);
        Assert.Equal("not-a-guid", payload.RootElement.GetProperty("tenant_id").GetString());
    }
}
