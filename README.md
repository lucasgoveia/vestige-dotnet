<p align="center">
  <h1 align="center">Vestige</h1>
  <p align="center">
    Wide event logging for .NET — one event per request, every field you need.
  </p>
  <p align="center">
    <a href="#quick-start">Quick Start</a> · <a href="#why-vestige">Why Vestige</a> · <a href="#packages">Packages</a> · <a href="#vestige-and-opentelemetry">OTel Integration</a> · <a href="#documentation">Docs</a>
  </p>
</p>

---

**Vestige** replaces scattered, low-context log lines with **wide events**: a single, rich structured event emitted once per request per service, containing everything you need to debug, alert, and analyze production traffic.

```
17 log lines that tell you nothing  →  1 event that tells you everything
```

Inspired by the [canonical log line](https://loggingsucks.com/) pattern (Stripe, Honeycomb) and built for the .NET ecosystem with a modular, Serilog-style package model.

## The Problem

A typical request produces dozens of log lines across your codebase. When something breaks, you grep through thousands of lines hoping to find context. You rarely do.

```
2026-02-10T14:23:45Z INFO  Incoming request method=GET path=/api/checkout
2026-02-10T14:23:45Z DEBUG JWT validation started
2026-02-10T14:23:45Z WARN  Slow query detected duration_ms=847
2026-02-10T14:23:46Z DEBUG Cache miss key=users:org_123
2026-02-10T14:23:46Z INFO  Request completed status=200 duration_ms=1247
2026-02-10T14:23:46Z ERROR Connection pool exhausted
... (12 more lines)
```

None of these lines alone tell you *why* the request was slow, *who* the user was, *what* they were trying to do, or *which* feature flags were active.

## The Fix

Vestige emits **one wide event** at the end of each request:

```json
{
  "timestamp": "2026-02-10T14:23:46.612Z",
  "service": "checkout-service",
  "version": "2.4.1",
  "trace_id": "abc123def456",
  "http.method": "POST",
  "http.path": "/api/checkout",
  "http.status_code": 200,
  "duration_ms": 1247,
  "outcome": "success",
  "user.id": "user_456",
  "user.subscription": "premium",
  "user.account_age_days": 847,
  "cart.id": "cart_xyz",
  "cart.total_cents": 15999,
  "cart.item_count": 3,
  "cart.coupon": "SAVE20",
  "payment.provider": "stripe",
  "payment.charge.duration_ms": 289,
  "payment.attempt": 1,
  "db.query_count": 3,
  "db.total_duration_ms": 124,
  "feature_flags.new_checkout": true
}
```

One event. High cardinality. High dimensionality. Queryable, not greppable.

## Quick Start

### Install

```bash
dotnet add package Vestige
dotnet add package Vestige.Extensions.AspNetCore
dotnet add package Vestige.Enrichers.HttpRequest
dotnet add package Vestige.Enrichers.HttpResponse
dotnet add package Vestige.Enrichers.Environment
dotnet add package Vestige.Sinks.Console
```

### Configure

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddVestige(options =>
    {
        options.ServiceName = "checkout-service";
        options.ServiceVersion = "2.4.1";
    })
    .AddHttpRequestEnricher()
    .AddHttpResponseEnricher()
    .AddEnvironmentEnricher()
    .AddConsoleSink();

var app = builder.Build();
app.UseVestige();
```

That's it. Every request now emits a wide event to your console with HTTP details, host info, timing, and status — zero custom instrumentation needed.

### Add Business Context

Inject `IWideEventAccessor` anywhere in your code to enrich the current event:

```csharp
public class CheckoutService
{
    private readonly IWideEventAccessor _vestige;
    private readonly IPaymentGateway _payments;

    public CheckoutService(IWideEventAccessor vestige, IPaymentGateway payments)
    {
        _vestige = vestige;
        _payments = payments;
    }

    public async Task<OrderResult> ProcessAsync(Cart cart, User user)
    {
        var ev = _vestige.Current;

        // Add business context — this is what makes wide events powerful
        ev?.Set("user.id", user.Id);
        ev?.Set("user.subscription", user.Subscription);
        ev?.Set("cart.id", cart.Id);
        ev?.Set("cart.total_cents", cart.TotalCents);
        ev?.Set("cart.item_count", cart.Items.Count);

        // Time sub-operations automatically
        using (ev?.Time("payment.charge"))
        {
            var result = await _payments.ChargeAsync(cart, user);
            ev?.Set("payment.provider", result.Provider);
            ev?.Set("payment.attempt", result.Attempt);
            return result;
        }
    }
}
```

The `?.` null-conditional pattern means this code works cleanly even in unit tests or background jobs where no event scope is active.

## Why Vestige

**Not another logging framework.** Vestige doesn't replace `ILogger`. It complements it with a fundamentally different approach: instead of logging *what your code is doing*, you record *what happened to this request*.

**DI-native.** No static `LogContext.Current`. Every component is resolved from the DI container — mockable, testable, replaceable.

**Modular.** Tiny core package with abstractions. Enrichers, sinks, and framework integrations are separate NuGet packages you opt into. Like Serilog, but for wide events.

**Tail sampling built in.** Keep 100% of errors, slow requests, and VIP users. Randomly sample the rest. Control costs without losing signal.

**OTel-friendly.** Optional `Vestige.Extensions.OpenTelemetry` package syncs your wide event fields onto OTel spans — richer traces with no infrastructure changes.

## Packages

### Core

| Package | Description |
|---|---|
| [`Vestige`](src/Vestige) | Abstractions, `WideEvent` model, async pipeline, tail sampler, JSON serializer, DI wiring. The only required package. |
| [`Vestige.Testing`](src/Vestige.Testing) | `InMemorySink`, `NullWideEventAccessor`, `TestWideEventAccessor` for unit and integration tests. |

### Framework Integrations

| Package | Description |
|---|---|
| [`Vestige.Extensions.AspNetCore`](src/Vestige.Extensions.AspNetCore) | Middleware, `UseVestige()`, scoped event accessor from `HttpContext`. |
| [`Vestige.Extensions.HttpClient`](src/Vestige.Extensions.HttpClient) | `DelegatingHandler` that auto-times outgoing HTTP calls (`http_out.*` fields). |
| [`Vestige.Extensions.EntityFrameworkCore`](src/Vestige.Extensions.EntityFrameworkCore) | Auto-captures `db.*` fields: query count, duration, slow queries, rows affected. |
| [`Vestige.Extensions.Hosting`](src/Vestige.Extensions.Hosting) | `IWideEventScopeFactory` for background services. Pipeline flush on shutdown. |
| [`Vestige.Extensions.OpenTelemetry`](src/Vestige.Extensions.OpenTelemetry) | Syncs wide event fields onto `Activity` (OTel span) as tags. |

### Enrichers

| Package | Fields Added |
|---|---|
| [`Vestige.Enrichers.HttpRequest`](src/Vestige.Enrichers.HttpRequest) | `http.method`, `http.path`, `http.query`, `http.user_agent`, `http.client_ip` |
| [`Vestige.Enrichers.HttpResponse`](src/Vestige.Enrichers.HttpResponse) | `http.status_code`, `http.bytes_sent`, `http.content_type` |
| [`Vestige.Enrichers.Identity`](src/Vestige.Enrichers.Identity) | `user.id`, `user.name`, `user.roles`, configurable claim mapping |
| [`Vestige.Enrichers.Activity`](src/Vestige.Enrichers.Activity) | `trace_id`, `span_id`, `parent_span_id`, `baggage.*` |
| [`Vestige.Enrichers.Environment`](src/Vestige.Enrichers.Environment) | `host.name`, `host.os`, `runtime.version`, `process.id` |

### Sinks

| Package | Description |
|---|---|
| [`Vestige.Sinks.Console`](src/Vestige.Sinks.Console) | JSON to stdout. Compact or indented. |
| [`Vestige.Sinks.File`](src/Vestige.Sinks.File) | JSON lines to rolling files. |
| [`Vestige.Sinks.Seq`](src/Vestige.Sinks.Seq) | Native Seq CLEF ingestion. |
| [`Vestige.Sinks.Kafka`](src/Vestige.Sinks.Kafka) | Publish events to Kafka. |
| [`Vestige.Sinks.EventHubs`](src/Vestige.Sinks.EventHubs) | Publish events to Azure Event Hubs. |

### Bridges

| Package | Description |
|---|---|
| [`Vestige.Bridges.Serilog`](src/Vestige.Bridges.Serilog) | Forward Serilog events to the current wide event. |
| [`Vestige.Bridges.MicrosoftLogging`](src/Vestige.Bridges.MicrosoftLogging) | Capture `ILogger` calls on the current wide event. |

## Sampling

Tail sampling is built into the core. Decisions happen *after* the request completes, so you can sample based on business context:

```csharp
builder.Services
    .AddVestige(options => { ... })
    .ConfigureSampling(sampling =>
    {
        sampling.AlwaysKeepErrors();
        sampling.AlwaysKeepSlowRequests(thresholdMs: 2000);
        sampling.AlwaysKeepWhen(e => e.Get<string>("user.subscription") == "enterprise");
        sampling.RateForPath("/healthz", 0.001);
        sampling.DefaultRate(0.05);
    });
```

## Full Example

```csharp
builder.Services
    .AddVestige(options =>
    {
        options.ServiceName = "checkout-service";
        options.ServiceVersion = "2.4.1";
        options.Region = "us-east-1";
    })
    // Enrichers
    .AddHttpRequestEnricher()
    .AddHttpResponseEnricher()
    .AddIdentityEnricher()
    .AddActivityEnricher()
    .AddEnvironmentEnricher()
    // Sinks
    .AddConsoleSink(o => o.Indented = builder.Environment.IsDevelopment())
    .AddKafkaSink(o =>
    {
        o.BootstrapServers = "kafka:9092";
        o.Topic = "wide-events";
    })
    // Optional: sync fields to OTel spans too
    .AddOpenTelemetrySync()
    // Sampling
    .ConfigureSampling(sampling =>
    {
        sampling.AlwaysKeepErrors();
        sampling.AlwaysKeepSlowRequests(2000);
        sampling.DefaultRate(0.05);
    })
    // Pipeline tuning
    .ConfigurePipeline(pipeline =>
    {
        pipeline.BufferCapacity = 10_000;
        pipeline.BatchSize = 100;
    });

var app = builder.Build();
app.UseVestige();
```

## Background Jobs

For non-HTTP contexts, use `IWideEventScopeFactory`:

```csharp
public class OrderProcessor : BackgroundService
{
    private readonly IWideEventScopeFactory _vestige;

    public OrderProcessor(IWideEventScopeFactory vestige) => _vestige = vestige;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var msg in _queue.ReadAllAsync(ct))
        {
            await using var scope = _vestige.BeginScope("job.process_order");
            var ev = scope.Event;

            ev.Set("job.type", "process_order");
            ev.Set("order.id", msg.OrderId);

            try
            {
                await ProcessAsync(msg, ct);
                ev.Outcome = "success";
            }
            catch (Exception ex)
            {
                ev.Outcome = "error";
                ev.CaptureException(ex);
            }
            // Event auto-emitted on dispose
        }
    }
}
```

## Vestige and OpenTelemetry

Vestige is a **logging library**. OpenTelemetry is a **telemetry protocol and SDK**. They're complementary.

Vestige's job is giving you a clean API to build wide events with rich business context and emit them to sinks. OTel's job is standardizing how telemetry moves across your infrastructure.

If your team uses OTel, the optional `Vestige.Extensions.OpenTelemetry` package syncs your wide event fields onto the current `Activity` span as tags. Your OTel backend (Jaeger, Honeycomb, Datadog, Tempo) then shows those fields on traces — no collector or pipeline changes needed.

```csharp
builder.Services
    .AddVestige(o => o.ServiceName = "checkout-service")
    .AddHttpRequestEnricher()
    .AddConsoleSink()                    // primary: Vestige emits wide events to sinks
    .AddOpenTelemetrySync();             // bonus: also enrich OTel spans with the same fields
```

**Before** Vestige (typical OTel span):

```
span: POST /api/checkout
├── http.method: POST
├── http.route: /api/checkout
├── http.status_code: 200
└── duration: 1247ms
```

**After** adding Vestige with OTel sync:

```
span: POST /api/checkout
├── http.method: POST
├── http.route: /api/checkout
├── http.status_code: 200
├── duration: 1247ms
├── user.id: user_789
├── user.subscription: premium
├── cart.total_cents: 16000
├── payment.provider: stripe
├── payment.attempt: 3
├── feature_flags.new_checkout: true
└── error.code: card_declined
```

Same code, same `ev.Set()` calls — the fields go to your sinks AND onto the OTel span.

**You don't need OTel to use Vestige.** Vestige works standalone with just sinks. The OTel sync is a one-line opt-in for teams that want both.

## Testing

```bash
dotnet add package Vestige.Testing
```

```csharp
// Integration test
var sink = new InMemorySink();

var app = new WebApplicationFactory<Program>()
    .WithWebHostBuilder(b =>
    {
        b.ConfigureServices(s =>
        {
            s.AddVestige(o => o.ServiceName = "test")
                .AddHttpRequestEnricher()
                .AddSink(sink);
        });
    });

var client = app.CreateClient();
await client.PostAsync("/checkout", content);

var ev = sink.Events.Single();
Assert.Equal(200, ev.Get<int>("http.status_code"));
Assert.Equal("user_123", ev.Get<string>("user.id"));
```

```csharp
// Unit test — no event scope, enrichment is silently ignored
var service = new CheckoutService(
    new NullWideEventAccessor(),
    mockPayments.Object);

await service.ProcessAsync(cart, user); // ev?.Set() is a no-op
```

## Extending Vestige

Vestige is designed for extension. Write your own enrichers, sinks, or bridges by implementing a single interface and providing an extension method on `VestigeBuilder`.

**Custom enricher:**

```csharp
public class FeatureFlagEnricher : WideEventEnricherBase
{
    private readonly IFeatureFlagService _flags;

    public FeatureFlagEnricher(IFeatureFlagService flags) => _flags = flags;

    public override async Task OnEventCreatedAsync(WideEvent ev, CancellationToken ct)
    {
        var userId = ev.Get<string>("user.id");
        if (userId is null) return;

        foreach (var (key, value) in await _flags.GetFlagsAsync(userId, ct))
            ev.Set($"feature_flags.{key}", value);
    }
}

public static class VestigeBuilderExtensions
{
    public static VestigeBuilder AddFeatureFlagEnricher(this VestigeBuilder builder)
    {
        builder.Services.AddSingleton<IWideEventEnricher, FeatureFlagEnricher>();
        return builder;
    }
}
```

**Custom sink:**

```csharp
public class RedisSink : IWideEventSink
{
    // Implement EmitAsync / EmitBatchAsync / FlushAsync
}

public static class VestigeBuilderExtensions
{
    public static VestigeBuilder AddRedisSink(
        this VestigeBuilder builder,
        Action<RedisSinkOptions> configure)
    {
        builder.Services.Configure(configure);
        builder.Services.AddSingleton<IWideEventSink, RedisSink>();
        return builder;
    }
}
```

See the [Writing Enrichers](docs/writing-enrichers.md), [Writing Sinks](docs/writing-sinks.md), and [OpenTelemetry Integration](docs/opentelemetry-integration.md) guides for full details.

## Documentation

- [Getting Started](docs/getting-started.md)
- [Architecture & Design](docs/architecture.md)
- [Writing Custom Enrichers](docs/writing-enrichers.md)
- [Writing Custom Sinks](docs/writing-sinks.md)
- [OpenTelemetry Integration](docs/opentelemetry-integration.md)
- [Sampling Strategies](docs/sampling.md)
- [Migrating from Serilog](docs/migration-from-serilog.md)

## Philosophy

> Instead of logging *what your code is doing*, log *what happened to this request*.

Vestige is built on the ideas behind [loggingsucks.com](https://loggingsucks.com/) and the canonical log line pattern pioneered at Stripe. Traditional logging is optimized for *writing* — `logger.Info("doing thing")` is easy in the moment. Vestige is optimized for *querying* — one structured event per request that answers any question you'll have at 2am during an outage.

## Contributing

Contributions are welcome. Please read the [contributing guide](CONTRIBUTING.md) before submitting a PR.

## License

[MIT](LICENSE)
