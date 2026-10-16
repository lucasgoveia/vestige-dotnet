using Vestige.Internal;

namespace Vestige.Sinks.IntegrationTests.Support;

public static class TestEvents
{
    public static WideEventData Make(Action<WideEvent>? configure = null)
    {
        var ev = new WideEvent { ServiceName = "integration", DurationMs = 2, Outcome = "success", StatusCode = 200 };
        ev.Set("http.method", "POST");
        ev.Set("http.path", "/tenants");
        configure?.Invoke(ev);
        return new SystemTextJsonSerializer().Serialize(ev);
    }
}
