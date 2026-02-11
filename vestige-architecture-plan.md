# Vestige — Architecture & Implementation Plan

## Wide Event Logging for .NET

---

## 1. What Vestige Is

Vestige is a **wide event logging library for .NET**. It implements the canonical log line pattern: instead of scattering dozens of low-context log lines per request, you accumulate one rich, structured event throughout the request lifecycle and emit it once at the end.

The primary output is a **JSON event sent to one or more sinks** — Console, File, Seq, Kafka, Azure Event Hubs, or anything you build.

### What Vestige Owns

**Business context.** The stuff only your application code knows: who the user is, what they're trying to do, which feature flags are active, what their cart contains, how many payment retries happened, what subscription tier they're on.

### What Vestige Does NOT Own

**Infrastructure instrumentation.** Database query timing, outgoing HTTP call tracing, gRPC client spans, connection pool metrics — this is OpenTelemetry's job. OTel has battle-tested auto-instrumentation libraries for SqlClient, Npgsql, HttpClient, EF Core, gRPC, and dozens more. Vestige doesn't duplicate that work.

### How They Meet

The optional `Vestige.Extensions.OpenTelemetry` package bridges both worlds:

- **Reads** OTel auto-instrumented Activity tags (db duration, HTTP status, etc.) **into** the WideEvent, so your wide event contains infrastructure data without you writing any code.
- **Writes** Vestige business context fields **onto** the Activity span, so your OTel backend shows rich business tags on traces.

```
┌──────────────────────────────┐  ┌──────────────────────────────┐
│       OpenTelemetry          │  │          Vestige              │
│                              │  │                               │
│  Auto-instrumentation:       │  │  Business context:            │
│  · SqlClient query timing    │  │  · ev.Set("user.id", ...)    │
│  · HttpClient call duration  │  │  · ev.Set("cart.total", ...) │
│  · EF Core commands          │  │  · ev.Set("payment.*", ...)  │
│  · gRPC client spans         │  │  · ev.Time("payment.charge") │
│                              │  │  · Enrichers (HTTP, identity) │
│  Sets Activity tags:         │  │                               │
│  · db.system, db.statement   │  │  Emits wide events to sinks: │
│  · http.method, http.url     │  │  · Console, File, Seq, Kafka │
│  · etc.                      │  │                               │
└──────────────┬───────────────┘  └───────────────┬───────────────┘
               │                                  │
               └──────────┐  ┌────────────────────┘
                          ▼  ▼
               ┌──────────────────────┐
               │  Vestige.Extensions  │
               │  .OpenTelemetry      │
               │                      │
               │  Reads OTel tags     │
               │  INTO WideEvent      │
               │                      │
               │  Writes Vestige      │
               │  fields ONTO Activity│
               └──────────────────────┘
                          │
                          ▼
               Wide event contains BOTH:
               · db.duration_ms (from OTel)
               · user.id (from your code)
               · http.status_code (from OTel)
               · cart.total_cents (from your code)
```

---

## 2. Ecosystem Model

Tiny core defining abstractions. Opt-in packages for enrichers, sinks, framework integrations, and OTel sync. Same decomposition pattern as Serilog.

```
┌─────────────────────────────────────────────────────────────────┐
│                        Your Application                         │
├─────────────────────────────────────────────────────────────────┤
│  Vestige.Extensions.AspNetCore                                  │
│  Vestige.Extensions.Hosting                                     │
│  Vestige.Extensions.OpenTelemetry (optional)                    │
├──────────────┬──────────────┬───────────────────────────────────┤
│  Enrichers   │    Sinks     │  Bridges                          │
│──────────────│──────────────│───────────────────────────────────│
│  .HttpRequest│  .Console    │  .Serilog                         │
│  .HttpResp.  │  .File       │  .MicrosoftLogging                │
│  .Identity   │  .Seq        │                                   │
│  .Activity   │  .Kafka      │                                   │
│  .Environment│  .EventHubs  │                                   │
├──────────────┴──────────────┴───────────────────────────────────┤
│                     Vestige (core)                               │
│    WideEvent · Enrichers · Sinks · Sampling · Pipeline · DI     │
└─────────────────────────────────────────────────────────────────┘
```

### Design Boundaries

| Concern | Owner | Why |
|---|---|---|
| Business context (user, cart, payment, flags) | **Vestige** | Only your code knows this |
| HTTP request/response metadata | **Vestige enrichers** | Extracted from `HttpContext`, part of the wide event workflow |
| User identity / claims | **Vestige enrichers** | Extracted from `ClaimsPrincipal` |
| Host / environment info | **Vestige enrichers** | Static info, no DiagnosticSource needed |
| Trace correlation (trace_id, span_id) | **Vestige enrichers** | Read from `Activity.Current` |
| DB query timing & details | **OpenTelemetry** | Auto-instrumentation via DiagnosticSource |
| Outgoing HTTP call timing | **OpenTelemetry** | Auto-instrumentation via DiagnosticSource |
| gRPC / messaging client spans | **OpenTelemetry** | Auto-instrumentation via DiagnosticSource |
| Bridging OTel ↔ Vestige | **Vestige.Extensions.OpenTelemetry** | Reads OTel tags in, writes Vestige fields out |

---

## 3. Package Map

### 3.1 Core

| Package | Dependencies | Description |
|---|---|---|
| **`Vestige`** | `M.E.DependencyInjection.Abstractions`, `M.E.Options`, `System.Text.Json` | All abstractions, `WideEvent` model, async pipeline, `TailSampler`, JSON serializer, DI wiring. The only required package. |
| **`Vestige.Testing`** | `Vestige` | `InMemorySink`, `NullWideEventAccessor`, `TestWideEventAccessor`, `WideEventAssertions`. |

### 3.2 Framework Integrations

| Package | Dependencies | Description |
|---|---|---|
| **`Vestige.Extensions.AspNetCore`** | `Vestige`, `M.AspNetCore.Http.Abstractions` | Middleware, `UseVestige()`, scoped accessor. |
| **`Vestige.Extensions.Hosting`** | `Vestige`, `M.E.Hosting.Abstractions` | `IWideEventScopeFactory` for background services, pipeline flush on shutdown. |
| **`Vestige.Extensions.OpenTelemetry`** | `Vestige`, `System.Diagnostics.DiagnosticSource` | Bidirectional sync: reads OTel Activity tags into WideEvent, writes Vestige fields onto Activity. |

### 3.3 Enrichers

Enrichers add fields that Vestige can extract **without** `DiagnosticSource` auto-instrumentation — data that comes from `HttpContext`, `ClaimsPrincipal`, `Activity.Current`, `Environment`, or your own services via DI.

| Package | Fields Added |
|---|---|
| **`Vestige.Enrichers.HttpRequest`** | `http.method`, `http.path`, `http.query`, `http.user_agent`, `http.client_ip`, `http.scheme`, `http.host` |
| **`Vestige.Enrichers.HttpResponse`** | `http.status_code`, `http.bytes_sent`, `http.content_type` |
| **`Vestige.Enrichers.Identity`** | `user.id`, `user.name`, `user.roles`, configurable claim mapping |
| **`Vestige.Enrichers.Activity`** | `trace_id`, `span_id`, `parent_span_id`, `baggage.*` |
| **`Vestige.Enrichers.Environment`** | `host.name`, `host.os`, `runtime.version`, `process.id` |
| **`Vestige.Enrichers.Thread`** | `thread.id`, `thread.name`, `thread.pool_count` |

Community examples (not shipped by us):

| Package (hypothetical) | Fields Added |
|---|---|
| `Vestige.Enrichers.LaunchDarkly` | `feature_flags.*` |
| `Vestige.Enrichers.Tenant` | `tenant.id`, `tenant.plan`, `tenant.region` |
| `Vestige.Enrichers.MassTransit` | `messaging.destination`, `messaging.message_id` |

### 3.4 Sinks

The **primary output path** for Vestige events.

| Package | Description |
|---|---|
| **`Vestige.Sinks.Console`** | JSON to stdout/stderr. Compact or indented. |
| **`Vestige.Sinks.File`** | JSON lines to rolling files. Max size, retention. |
| **`Vestige.Sinks.Seq`** | Native Seq CLEF ingestion over HTTP. |
| **`Vestige.Sinks.Kafka`** | Publish JSON events to Kafka topics. |
| **`Vestige.Sinks.EventHubs`** | Publish to Azure Event Hubs. |
| **`Vestige.Sinks.RabbitMQ`** | Publish to RabbitMQ exchange. |

Community examples:

| Package (hypothetical) | Description |
|---|---|
| `Vestige.Sinks.ClickHouse` | Direct insert into ClickHouse. |
| `Vestige.Sinks.Loki` | Push to Grafana Loki. |
| `Vestige.Sinks.Elasticsearch` | Index into Elasticsearch/OpenSearch. |

### 3.5 Bridges (Migration Helpers)

| Package | Description |
|---|---|
| **`Vestige.Bridges.Serilog`** | Forward Serilog events as fields on the current `WideEvent`. Migration tool for codebases with extensive Serilog usage. |
| **`Vestige.Bridges.MicrosoftLogging`** | Capture `ILogger` calls on the current wide event. |

---

## 4. Core Abstractions

### 4.1 `WideEvent` — The Event Model

```csharp
public sealed class WideEvent
{
    // --- Fixed fields (always present) ---
    public string EventId { get; }
    public DateTimeOffset Timestamp { get; }
    public string ServiceName { get; init; }
    public string? ServiceVersion { get; init; }
    public string? Region { get; init; }
    public string? DeploymentId { get; init; }

    // --- Trace context (populated by Activity enricher if present) ---
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }

    // --- Request lifecycle (set by middleware) ---
    public string? RequestId { get; set; }
    public int? StatusCode { get; set; }
    public long? DurationMs { get; set; }
    public string? Outcome { get; set; }  // "success" | "error" | "timeout"

    // --- Open property bag (the "wide" part) ---
    private readonly ConcurrentDictionary<string, object?> _properties = new();

    public void Set(string key, object? value);
    public void SetMany(IEnumerable<KeyValuePair<string, object?>> properties);
    public T? Get<T>(string key);
    public bool Has(string key);
    public void Remove(string key);
    public IReadOnlyDictionary<string, object?> Properties { get; }

    // --- Sub-timers ---
    private readonly ConcurrentDictionary<string, long> _timings = new();
    public IDisposable Time(string operationName);
    public void RecordTiming(string operationName, long milliseconds);
    public IReadOnlyDictionary<string, long> Timings { get; }

    // --- Error capture ---
    public void CaptureException(Exception ex, bool isPrimary = true);
}
```

### 4.2 All Interfaces

```csharp
// --- Event access (DI-based, no statics) ---
public interface IWideEventAccessor
{
    WideEvent? Current { get; }
}

internal interface IWideEventAccessorSetter : IWideEventAccessor
{
    void Set(WideEvent wideEvent);
    void Clear();
}

// --- Event factory ---
public interface IWideEventFactory
{
    WideEvent Create();
}

// --- Enrichers ---
public interface IWideEventEnricher
{
    Task OnEventCreatedAsync(WideEvent wideEvent, CancellationToken ct = default);
    Task OnBeforeEmitAsync(WideEvent wideEvent, CancellationToken ct = default);
}

public abstract class WideEventEnricherBase : IWideEventEnricher
{
    public virtual Task OnEventCreatedAsync(WideEvent ev, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnBeforeEmitAsync(WideEvent ev, CancellationToken ct) => Task.CompletedTask;
}

// --- Sinks (primary output) ---
public interface IWideEventSink : IAsyncDisposable
{
    string Name { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task EmitAsync(WideEventData eventData, CancellationToken ct = default);
    Task EmitBatchAsync(IReadOnlyList<WideEventData> events, CancellationToken ct = default);
    Task FlushAsync(CancellationToken ct = default);
}

// --- Sampling ---
public interface ISamplingStrategy
{
    SamplingDecision Evaluate(WideEvent wideEvent);
}

public enum SamplingDecision { Keep, Drop, Defer }

// --- Pipeline ---
public interface IWideEventPipeline
{
    Task EmitAsync(WideEvent wideEvent, CancellationToken ct = default);
}

// --- Scoping (non-HTTP contexts) ---
public interface IWideEventScopeFactory
{
    IWideEventScope BeginScope(string scopeName);
}

public interface IWideEventScope : IAsyncDisposable
{
    WideEvent Event { get; }
}

// --- Serialization ---
public interface IWideEventSerializer
{
    ReadOnlyMemory<byte> Serialize(WideEvent wideEvent);
    string ContentType { get; }
}

// --- Serialization-ready DTO ---
public sealed class WideEventData
{
    public IReadOnlyDictionary<string, object?> Fields { get; }
    public ReadOnlyMemory<byte> JsonBytes { get; }
    public WideEvent OriginalEvent { get; }
}
```

### 4.3 The Builder

```csharp
public class VestigeBuilder
{
    public IServiceCollection Services { get; }
    public VestigeOptions Options { get; }
}

public class VestigeOptions
{
    public string ServiceName { get; set; } = "unknown";
    public string? ServiceVersion { get; set; }
    public string? Region { get; set; }
    public string? DeploymentId { get; set; }
    public string? Environment { get; set; }
}

public static class VestigeServiceCollectionExtensions
{
    public static VestigeBuilder AddVestige(
        this IServiceCollection services,
        Action<VestigeOptions>? configure = null)
    {
        var options = new VestigeOptions();
        configure?.Invoke(options);

        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IWideEventFactory, WideEventFactory>();
        services.AddScoped<IWideEventAccessor, WideEventAccessor>();
        services.AddSingleton<IWideEventPipeline, WideEventPipeline>();
        services.AddSingleton<IWideEventSerializer, SystemTextJsonSerializer>();
        services.AddSingleton<IWideEventScopeFactory, WideEventScopeFactory>();

        return new VestigeBuilder(services, options);
    }
}
```

---

## 5. Pipeline Execution

```
Request arrives
│
├── 1. Middleware creates WideEvent, sets on accessor, starts stopwatch
│
├── 2. Enrichers.OnEventCreated()
│      HTTP method/path, user identity, trace_id, environment
│      (OTel sync: reads existing Activity tags into WideEvent)
│
├── 3. Handler runs — application code calls ev.Set() / ev.Time()
│      user.id, cart.total_cents, payment.provider, feature_flags, etc.
│
├── 4. Request completes (or throws)
│      Middleware sets duration_ms, status_code, outcome
│
├── 5. Enrichers.OnBeforeEmit()
│      http.bytes_sent, http.content_type
│      (OTel sync: writes all Vestige fields onto Activity as tags)
│
├── 6. Tail sampling evaluates keep/drop
│
├── 7. If kept:
│      a. Serialize → WideEventData (JSON bytes, computed once)
│      b. Dispatch to all sinks in parallel
│
└── 8. Clear accessor
```

### Batching & Backpressure

```csharp
builder.Services
    .AddVestige(options => { ... })
    .ConfigurePipeline(pipeline =>
    {
        pipeline.BufferCapacity = 10_000;
        pipeline.BatchSize = 100;
        pipeline.BatchFlushInterval = TimeSpan.FromMilliseconds(500);
        pipeline.OnBufferFull = BufferFullBehavior.Drop;
    });
```

---

## 6. Enrichers

### 6.1 Interface & Registration

```csharp
public interface IWideEventEnricher
{
    Task OnEventCreatedAsync(WideEvent wideEvent, CancellationToken ct = default);
    Task OnBeforeEmitAsync(WideEvent wideEvent, CancellationToken ct = default);
}
```

```csharp
builder.Services
    .AddVestige(options => { ... })
    .AddHttpRequestEnricher()
    .AddHttpResponseEnricher()
    .AddIdentityEnricher(o => o.ClaimMappings["tenant"] = "user.tenant_id")
    .AddActivityEnricher()
    .AddEnvironmentEnricher();
```

### 6.2 Enricher Ordering

```csharp
.AddIdentityEnricher(order: 10)
.AddFeatureFlagEnricher(order: 20)  // runs after identity, can read user.id
```

### 6.3 Writing a Custom Enricher

```csharp
// NuGet: Vestige.Enrichers.FeatureFlags
namespace Vestige.Enrichers.FeatureFlags;

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

---

## 7. Sinks

### 7.1 Interface & Registration

```csharp
public interface IWideEventSink : IAsyncDisposable
{
    string Name { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task EmitAsync(WideEventData eventData, CancellationToken ct = default);
    Task EmitBatchAsync(IReadOnlyList<WideEventData> events, CancellationToken ct = default);
    Task FlushAsync(CancellationToken ct = default);
}
```

```csharp
builder.Services
    .AddVestige(options => { ... })
    .AddConsoleSink(o => o.Indented = true)
    .AddKafkaSink(o =>
    {
        o.BootstrapServers = "kafka:9092";
        o.Topic = "wide-events";
    });
```

### 7.2 Writing a Custom Sink

```csharp
// NuGet: Vestige.Sinks.EventHubs
namespace Vestige.Sinks.EventHubs;

public class EventHubSink : IWideEventSink
{
    private readonly EventHubProducerClient _producer;
    private readonly EventHubSinkOptions _options;

    public string Name => $"EventHub({_options.EventHubName})";

    public EventHubSink(IOptions<EventHubSinkOptions> options)
    {
        _options = options.Value;
        _producer = new EventHubProducerClient(
            _options.ConnectionString, _options.EventHubName);
    }

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task EmitAsync(WideEventData eventData, CancellationToken ct)
    {
        var batch = await _producer.CreateBatchAsync(ct);
        batch.TryAdd(new EventData(eventData.JsonBytes));
        await _producer.SendAsync(batch, ct);
    }

    public async Task EmitBatchAsync(IReadOnlyList<WideEventData> events, CancellationToken ct)
    {
        var batch = await _producer.CreateBatchAsync(ct);
        foreach (var eventData in events)
        {
            if (!batch.TryAdd(new EventData(eventData.JsonBytes)))
            {
                await _producer.SendAsync(batch, ct);
                batch = await _producer.CreateBatchAsync(ct);
                batch.TryAdd(new EventData(eventData.JsonBytes));
            }
        }
        if (batch.Count > 0)
            await _producer.SendAsync(batch, ct);
    }

    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public async ValueTask DisposeAsync() => await _producer.DisposeAsync();
}

public class EventHubSinkOptions
{
    public string ConnectionString { get; set; } = "";
    public string EventHubName { get; set; } = "wide-events";
}

public static class VestigeBuilderExtensions
{
    public static VestigeBuilder AddEventHubSink(
        this VestigeBuilder builder,
        Action<EventHubSinkOptions> configure)
    {
        builder.Services.Configure(configure);
        builder.Services.AddSingleton<IWideEventSink, EventHubSink>();
        return builder;
    }
}
```

---

## 8. Sampling

### 8.1 Built-In: TailSampler

```csharp
builder.Services
    .AddVestige(options => { ... })
    .ConfigureSampling(sampling =>
    {
        sampling.AlwaysKeepErrors();
        sampling.AlwaysKeepSlowRequests(thresholdMs: 2000);
        sampling.AlwaysKeepWhen(e => e.Get<string>("user.subscription") == "enterprise");
        sampling.AlwaysKeepPaths("/api/checkout", "/api/payments/*");
        sampling.RateForPath("/healthz", 0.001);
        sampling.DefaultRate(0.05);
    });
```

### 8.2 Custom Strategy

```csharp
public class AdaptiveSampler : ISamplingStrategy
{
    private readonly IErrorRateTracker _errorRate;

    public AdaptiveSampler(IErrorRateTracker errorRate) => _errorRate = errorRate;

    public SamplingDecision Evaluate(WideEvent ev)
    {
        if (ev.StatusCode >= 500) return SamplingDecision.Keep;

        var rate = _errorRate.GetCurrentRate() > 0.05 ? 0.50 : 0.05;
        return Random.Shared.NextDouble() < rate
            ? SamplingDecision.Keep
            : SamplingDecision.Drop;
    }
}

builder.Services
    .AddVestige(options => { ... })
    .UseSamplingStrategy<AdaptiveSampler>();
```

### 8.3 Composable Chain

```csharp
.UseSamplingStrategy<AlwaysKeepErrorsSampler>()   // Keep or Defer
.UseSamplingStrategy<VipUserSampler>()             // Keep or Defer
.UseSamplingStrategy<RandomRateSampler>()          // Keep or Drop (terminal)
```

---

## 9. OpenTelemetry Integration (Optional)

`Vestige.Extensions.OpenTelemetry` is an optional package that bridges Vestige and OTel bidirectionally.

### 9.1 What It Does

**Reads IN**: At event creation, imports existing `Activity` tags (auto-instrumented by OTel) into the `WideEvent`. DB query timing, outgoing HTTP call details, gRPC metadata — all of it becomes queryable fields on the wide event without you writing any code.

**Writes OUT**: Before emit, copies all `WideEvent` fields onto the `Activity` as span tags. Your OTel backend (Jaeger, Honeycomb, Datadog, Tempo) shows business context on traces.

### 9.2 Implementation

```csharp
public class OpenTelemetrySyncEnricher : WideEventEnricherBase
{
    private readonly OpenTelemetrySyncOptions _options;

    public OpenTelemetrySyncEnricher(IOptions<OpenTelemetrySyncOptions> options)
        => _options = options.Value;

    public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken ct)
    {
        var activity = Activity.Current;
        if (activity is null || !_options.ReadActivityTags) return Task.CompletedTask;

        foreach (var tag in activity.TagObjects)
        {
            if (_options.ExcludePrefixes.Any(p => tag.Key.StartsWith(p)))
                continue;
            ev.Set(tag.Key, tag.Value);
        }

        return Task.CompletedTask;
    }

    public override Task OnBeforeEmitAsync(WideEvent ev, CancellationToken ct)
    {
        var activity = Activity.Current;
        if (activity is null || !_options.WriteToActivity) return Task.CompletedTask;

        foreach (var (key, value) in ev.Properties)
        {
            if (_options.ExcludePrefixes.Any(p => key.StartsWith(p)))
                continue;
            activity.SetTag(key, value);
        }

        foreach (var (key, ms) in ev.Timings)
            activity.SetTag($"{key}.duration_ms", ms);

        return Task.CompletedTask;
    }
}
```

### 9.3 Usage

```csharp
builder.Services
    .AddVestige(o => o.ServiceName = "checkout-service")
    .AddHttpRequestEnricher()
    .AddConsoleSink()
    .AddOpenTelemetrySync(o =>
    {
        o.ReadActivityTags = true;          // import OTel auto-instrumented data
        o.WriteToActivity = true;           // enrich OTel spans with Vestige fields
        o.ExcludePrefixes = ["internal."];  // don't sync internal fields
    });
```

### 9.4 Result

Wide event now contains fields from **both** Vestige and OTel auto-instrumentation:

```json
{
  "service": "checkout-service",
  "duration_ms": 1247,
  "outcome": "success",

  "user.id": "user_789",
  "user.subscription": "premium",
  "cart.total_cents": 16000,
  "payment.provider": "stripe",
  "payment.charge.duration_ms": 1089,
  "feature_flags.new_checkout": true,

  "db.system": "postgresql",
  "db.statement": "SELECT * FROM carts WHERE user_id = $1",
  "db.duration_ms": 47,
  "http.url": "https://api.stripe.com/v1/charges",
  "http.status_code": 200
}
```

Business context (top half) from Vestige. Infrastructure details (bottom half) imported from OTel. One event, complete picture.

### 9.5 Without OTel

Vestige works standalone. If you don't use OTel, you just don't install the extension. Your wide events contain whatever your enrichers and `ev.Set()` calls provide. You can always add OTel + the sync extension later.

---

## 10. Dependency Injection — No Statics

### 10.1 Accessing the Current Event

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

        ev?.Set("user.id", user.Id);
        ev?.Set("user.subscription", user.Subscription);
        ev?.Set("cart.id", cart.Id);
        ev?.Set("cart.total_cents", cart.TotalCents);

        using (ev?.Time("payment.charge"))
        {
            var result = await _payments.ChargeAsync(cart, user);
            ev?.Set("payment.provider", result.Provider);
            return result;
        }
    }
}
```

### 10.2 Background Jobs

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
        }
    }
}
```

---

## 11. ASP.NET Core Middleware

```csharp
public class WideEventMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWideEventFactory _factory;
    private readonly IWideEventPipeline _pipeline;
    private readonly IEnumerable<IWideEventEnricher> _enrichers;

    public async Task InvokeAsync(HttpContext context, IWideEventAccessorSetter accessor)
    {
        var ev = _factory.Create();
        accessor.Set(ev);
        var sw = Stopwatch.StartNew();

        try
        {
            foreach (var enricher in _enrichers)
                await enricher.OnEventCreatedAsync(ev, context.RequestAborted);

            await _next(context);
            ev.Outcome = "success";
        }
        catch (Exception ex)
        {
            ev.Outcome = "error";
            ev.CaptureException(ex);
            throw;
        }
        finally
        {
            sw.Stop();
            ev.DurationMs = sw.ElapsedMilliseconds;
            ev.StatusCode = context.Response.StatusCode;

            foreach (var enricher in _enrichers)
                await enricher.OnBeforeEmitAsync(ev, context.RequestAborted);

            await _pipeline.EmitAsync(ev, CancellationToken.None);
            accessor.Clear();
        }
    }
}
```

---

## 12. Extension Package Conventions

Every extension package follows the same pattern:

1. Reference only `Vestige` core (plus framework dependencies).
2. Provide an extension method on `VestigeBuilder`.
3. Use `IOptions<T>` for configuration.
4. Support constructor injection.
5. Return `VestigeBuilder` for chaining.
6. Follow naming: `Vestige.{Category}.{Name}`.

### Enricher Package Structure

```
Vestige.Enrichers.HttpRequest/
├── HttpRequestEnricher.cs
├── HttpRequestEnricherOptions.cs
└── VestigeBuilderExtensions.cs
```

### Sink Package Structure

```
Vestige.Sinks.Kafka/
├── KafkaSink.cs
├── KafkaSinkOptions.cs
└── VestigeBuilderExtensions.cs
```

---

## 13. Testing

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
ev.Should().HaveField("cart.total_cents", 15999);
ev.Should().HaveOutcome("success");
```

```csharp
// Unit test — no event scope, ev?.Set() is a no-op
var service = new CheckoutService(new NullWideEventAccessor(), mockPayments.Object);
await service.ProcessAsync(cart, user);
```

```csharp
// Unit test — capture enrichment for assertions
var accessor = new TestWideEventAccessor();
var service = new CheckoutService(accessor, mockPayments.Object);
await service.ProcessAsync(cart, user);
Assert.Equal("premium", accessor.Current!.Get<string>("user.subscription"));
```

---

## 14. Solution & Repository Structure

```
vestige/
├── src/
│   ├── Vestige/                                  # Core abstractions + pipeline
│   ├── Vestige.Testing/                          # Test helpers
│   │
│   ├── Vestige.Extensions.AspNetCore/            # Middleware
│   ├── Vestige.Extensions.Hosting/               # Background service support
│   ├── Vestige.Extensions.OpenTelemetry/         # Bidirectional Activity sync
│   │
│   ├── Vestige.Enrichers.HttpRequest/
│   ├── Vestige.Enrichers.HttpResponse/
│   ├── Vestige.Enrichers.Identity/
│   ├── Vestige.Enrichers.Activity/
│   ├── Vestige.Enrichers.Environment/
│   ├── Vestige.Enrichers.Thread/
│   │
│   ├── Vestige.Sinks.Console/
│   ├── Vestige.Sinks.File/
│   ├── Vestige.Sinks.Seq/
│   ├── Vestige.Sinks.Kafka/
│   ├── Vestige.Sinks.EventHubs/
│   │
│   ├── Vestige.Bridges.Serilog/
│   └── Vestige.Bridges.MicrosoftLogging/
│
├── tests/
│   ├── Vestige.Tests/
│   ├── Vestige.Extensions.AspNetCore.Tests/
│   ├── Vestige.Extensions.OpenTelemetry.Tests/
│   ├── Vestige.Enrichers.HttpRequest.Tests/
│   ├── Vestige.Sinks.Kafka.Tests/
│   └── Vestige.IntegrationTests/
│
├── samples/
│   ├── Vestige.Samples.WebApi/                   # Minimal API + Console sink
│   ├── Vestige.Samples.Worker/                   # Background service
│   └── Vestige.Samples.WithOpenTelemetry/        # Vestige + OTel together
│
├── docs/
│   ├── getting-started.md
│   ├── writing-enrichers.md
│   ├── writing-sinks.md
│   ├── opentelemetry-integration.md
│   ├── sampling.md
│   └── migration-from-serilog.md
│
├── Directory.Build.props
├── Directory.Packages.props
├── Vestige.sln
└── README.md
```

---

## 15. Implementation Phases

### Phase 1 — Core + Console Sink + Essentials

**Packages**: `Vestige`, `Vestige.Testing`, `Vestige.Extensions.AspNetCore`, `Vestige.Extensions.Hosting`, `Vestige.Enrichers.HttpRequest`, `Vestige.Enrichers.HttpResponse`, `Vestige.Enrichers.Activity`, `Vestige.Enrichers.Environment`, `Vestige.Sinks.Console`.

**Milestone**: `dotnet add` a few packages, three lines of setup, rich wide events in the console.

### Phase 2 — Persistence Sinks

**Packages**: `Vestige.Sinks.File`, `Vestige.Sinks.Seq`, `Vestige.Sinks.Kafka`, `Vestige.Sinks.EventHubs`.

### Phase 3 — OTel Integration & Identity

**Packages**: `Vestige.Extensions.OpenTelemetry`, `Vestige.Enrichers.Identity`, `Vestige.Enrichers.Thread`.

### Phase 4 — Bridges & Polish

**Packages**: `Vestige.Bridges.Serilog`, `Vestige.Bridges.MicrosoftLogging`.

Plus: source-generated JSON serialization, object pooling, benchmarks (target < 5μs per request), documentation site, contributor guide.