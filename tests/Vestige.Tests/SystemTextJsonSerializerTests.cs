using System.Text.Json;
using Vestige;
using Vestige.Internal;

namespace Vestige.Tests;

public sealed class SystemTextJsonSerializerTests
{
    private readonly SystemTextJsonSerializer _serializer = new();

    [Fact]
    public void Serialize_IncludesFixedFields()
    {
        var ev = new WideEvent { ServiceName = "svc", Outcome = "success" };
        var data = _serializer.Serialize(ev);

        Assert.True(data.Fields.ContainsKey("event_id"));
        Assert.True(data.Fields.ContainsKey("timestamp"));
        Assert.Equal("svc", data.Fields["service.name"]);
        Assert.Equal("success", data.Fields["outcome"]);
    }

    [Fact]
    public void Serialize_IncludesDynamicProperties()
    {
        var ev = new WideEvent();
        ev.Set("user.id", "u1");
        ev.Set("order.total", 99.99);

        var data = _serializer.Serialize(ev);

        Assert.Equal("u1", data.Fields["user.id"]);
        Assert.Equal(99.99, data.Fields["order.total"]);
    }

    [Fact]
    public void Serialize_TimingsPrefixedWithTimer()
    {
        var ev = new WideEvent();
        using (ev.Time("db.query")) { /* instant */ }

        var data = _serializer.Serialize(ev);

        Assert.True(data.Fields.ContainsKey("timer.db.query"));
    }

    [Fact]
    public void Serialize_NullFixedFieldsOmitted()
    {
        var ev = new WideEvent(); // TraceId, SpanId, ServiceName, Outcome all null
        var data = _serializer.Serialize(ev);

        Assert.False(data.Fields.ContainsKey("service.name"));
        Assert.False(data.Fields.ContainsKey("trace_id"));
        Assert.False(data.Fields.ContainsKey("outcome"));
    }

    [Fact]
    public void Serialize_JsonBytesIsValidJson()
    {
        var ev = new WideEvent { ServiceName = "test", Outcome = "success" };
        ev.Set("key", "value");
        var data = _serializer.Serialize(ev);

        // Should not throw
        var doc = JsonDocument.Parse(data.JsonBytes);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Serialize_OriginalEventSet()
    {
        var ev = new WideEvent();
        var data = _serializer.Serialize(ev);
        Assert.Same(ev, data.OriginalEvent);
    }

    [Fact]
    public void Serialize_DurationMsAlwaysPresent()
    {
        var ev = new WideEvent { DurationMs = 123.4 };
        var data = _serializer.Serialize(ev);
        Assert.Equal(123.4, data.Fields["duration_ms"]);
    }
}
