# Vestige — Architecture & Implementation Plan

## Wide Event Logging for .NET

---

## 1. What Vestige Is

Vestige is a **wide event logging library for .NET**. It implements the canonical log line pattern: instead of scattering dozens of low-context log lines per request, you accumulate one rich, structured event throughout the request lifecycle and emit it once at the end.

The primary output is a **JSON event sent to one or more sinks** — Console, File, Seq, Kafka, Azure Event Hubs, or anything you build. This is the core of Vestige: a logging library optimized for high-cardinality, high-dimensionality structured events.

As an optional extension, Vestige can also sync its fields onto the current OpenTelemetry `Activity` span, so teams with existing OTel pipelines get richer spans for free without changing their infrastructure.

### Key Tenets (from [loggingsucks.com](https://loggingsucks.com/))

- **One event per request per service** — not dozens of log lines, one comprehensive record.
- **High cardinality** — fields like `user.id`, `order.id`, `trace_id` with millions of unique values.
- **High dimensionality** — 50–100+ fields per event covering infrastructure, business context, errors, timings, and feature flags.
- **Tail sampling** — keep 100% of errors/slow/VIP requests, randomly sample the rest.
- **Queryable, not greppable** — structured JSON for columnar stores, not string search.

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                          Your Application                           │
│                                                                     │
│   ev.Set("user.id", userId)                                         │
│   ev.Set("cart.total_cents", 15999)                                 │
│   using (ev.Time("payment.charge")) { ... }                        │
│                                                                     │
├─────────────────────────────────────────────────────────────────────┤
│                         Vestige Core                                │
│                                                                     │
│   WideEvent accumulates all fields in a ConcurrentDictionary        │
│   Enrichers add fields at request start and end                     │
│   Tail sampler evaluates keep/drop after request completes          │
│   Pipeline serializes → dispatches to sinks                         │
│                                                                     │
├─────────────────────────────────────────────────────────────────────┤
│                         Sinks (primary output)                      │
│                                                                     │
│   Console · File · Seq · Kafka · EventHubs · RabbitMQ · Custom      │
│   Each sink receives pre-serialized JSON and writes to its target   │
│                                                                     │
├─────────────────────────────────────────────────────────────────────┤
│              Optional: OTel Activity Sync Extension                  │
│                                                                     │
│   Vestige.Extensions.OpenTelemetry syncs WideEvent fields onto      │
│   Activity.Current as span tags — enriching existing OTel spans     │
│   with the same business context, no infrastructure changes needed  │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 3. Ecosystem Model

Tiny core defining abstractions. Opt-in packages for enrichers, sinks, framework integrations, and OTel sync. Same decomposition pattern as Serilog.

```
┌─────────────────────────────────────────────────────────────────┐
│                        Your Application                         │
├─────────────────────────────────────────────────────────────────┤
│  Vestige.Extensions.AspNetCore                                  │
│  Vestige.Extensions.HttpClient                                  │
│  Vestige.Extensions.EntityFrameworkCore                         │
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

### Why This Matters

- **Minimal dependency surface** — an app using Console + HttpRequest enricher pulls in 3 tiny packages.
- **Independent versioning** — a bug fix to the Kafka sink ships without touching core.
- **Community extensions** — anyone can publish `Vestige.Enrichers.LaunchDarkly` or `Vestige.Sinks.ClickHouse`.
- **OTel is optional** — you don't need OpenTelemetry to use Vestige. But if you have it, the sync extension makes your spans richer for free.

---

## 4. Package Map

### 4.1 Core

| Package | Dependencies | Description |
|---|---|---|
| **`Vestige`** | `M.E.DependencyInjection.Abstractions`, `M.E.Options`, `System.Text.Json` | All abstractions (`IWideEventSink`, `IWideEventEnricher`, `ISamplingStrategy`, `IWideEventSerializer`, `IWideEventAccessor`, `IWideEventFactory`, `IWideEventPipeline`, `IWideEventScopeFactory`), the `WideEvent` model, async pipeline engine, `TailSampler`, default JSON serializer, and DI wiring. The only required package. |
| **`Vestige.Testing`** | `Vestige` | `InMemorySink`, `NullWideEventAccessor`, `TestWideEventAccessor`, `WideEventAssertions`. |

### 4.2 Framework Integrations

| Package | Dependencies | Description |
|---|---|---|
| **`Vestige.Extensions.AspNetCore`** | `Vestige`, `M.AspNetCore.Http.Abstractions` | Middleware, `UseVestige()`, scoped accessor. |
| **`Vestige.Extensions.HttpClient`** | `Vestige`, `M.E.Http` | `DelegatingHandler` that auto-times outgoing calls and writes `http_out.*` fields. |
| **`Vestige.Extensions.EntityFrameworkCore`** | `Vestige`, `M.EntityFrameworkCore` | `DiagnosticSource` listener that captures `db.*` fields. |
| **`Vestige.Extensions.Hosting`** | `Vestige`, `M.E.Hosting.Abstractions` | `IWideEventScopeFactory` for background services, pipeline flush on shutdown. |
| **`Vestige.Extensions.OpenTelemetry`** | `Vestige`, `System.Diagnostics.DiagnosticSource` | Syncs `WideEvent` fields onto `Activity.Current` as span tags. Optional layer for teams with existing OTel pipelines. |

### 4.3 Enrichers

Each enricher is its own package. Depends on `Vestige` core plus whatever framework it reads from.

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

### 4.4 Sinks

Each sink is its own package. The **primary output path** for Vestige events.

| Package | Description |
|---|---|
| **`Vestige.Sinks.Console`** | JSON to stdout/stderr. Compact or indented. |
| **`Vestige.Sinks.File`** | JSON lines to rolling files. Max size, retention, async flush. |
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

### 4.5 Bridges (Migration Helpers)

| Package | Description |
|---|---|
| **`Vestige.Bridges.Serilog`** | Forward Serilog events as fields on the current `WideEvent`. |
| **`Vestige.Bridges.MicrosoftLogging`** | Capture `ILogger` calls on the current wide event. |

---

## 5. Core Abstractions

### 5.1 `WideEvent` — The Event Model

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

Design notes:

- `ConcurrentDictionary` for thread safety — enrichers can write from different threads.
- Flat dot-separated keys (`user.id`, `payment.provider`) map directly to columnar store columns.
- `CaptureException` extracts type, message, code, stack trace into `error.*` fields.

### 5.2 All Interfaces

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

// --- Sinks (primary output — receive pre-serialized JSON) ---
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

### 5.3 The Builder

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

All extension packages provide extension methods on `VestigeBuilder`, returning it for chaining.

---

## 6. Pipeline Execution

```
Request arrives
│
├── 1. Middleware creates WideEvent via IWideEventFactory
│      Sets it on IWideEventAccessorSetter
│      Starts Stopwatch
│
├── 2. Enrichers.OnEventCreated() — add context available at request start
│      (HTTP method, path, user claims, trace_id, environment, etc.)
│
├── 3. Handler runs — application code calls ev.Set() / ev.Time()
│      (user.id, cart.total_cents, payment.provider, feature_flags, etc.)
│
├── 4. Request completes (or throws)
│      Middleware sets duration_ms, status_code, outcome
│
├── 5. Enrichers.OnBeforeEmit() — add context available at request end
│      (http.bytes_sent, http.content_type, etc.)
│
├── 6. Tail sampling evaluates (keep/drop)
│      Based on status, duration, business fields
│
├── 7. If kept:
│      a. Serialize via IWideEventSerializer → WideEventData (JSON bytes)
│      b. Dispatch to all registered IWideEventSink instances
│      c. (Optional) If OTel extension is enabled, sync fields to Activity
│
└── 8. Clear WideEvent from accessor
```

### Batching & Backpressure

The pipeline uses `System.Threading.Channels.Channel<WideEventData>` to decouple production from sink emission:

```csharp
builder.Services
    .AddVestige(options => { ... })
    .ConfigurePipeline(pipeline =>
    {
        pipeline.BufferCapacity = 10_000;
        pipeline.BatchSize = 100;
        pipeline.BatchFlushInterval = TimeSpan.FromMilliseconds(500);
        pipeline.OnBufferFull = BufferFullBehavior.Drop; // Drop | Block | DropOldest
    });
```

---

## 7. Enrichers

### 7.1 Interface & Registration

```csharp
public interface IWideEventEnricher
{
    /// Called at request start. Add context available immediately.
    Task OnEventCreatedAsync(WideEvent wideEvent, CancellationToken ct = default);

    /// Called just before emit. Add context available after processing.
    Task OnBeforeEmitAsync(WideEvent wideEvent, CancellationToken ct = default);
}
```

Registered via extension methods on `VestigeBuilder`:

```csharp
builder.Services
    .AddVestige(options => { ... })
    .AddHttpRequestEnricher()
    .AddHttpResponseEnricher()
    .AddIdentityEnricher(o => o.ClaimMappings["tenant"] = "user.tenant_id")
    .AddActivityEnricher()
    .AddEnvironmentEnricher();
```

### 7.2 Enricher Ordering

Enrichers execute in registration order. Explicit ordering is available:

```csharp
.AddIdentityEnricher(order: 10)
.AddFeatureFlagEnricher(order: 20)  // runs after identity, can read user.id
```

### 7.3 Writing a Custom Enricher

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

## 8. Sinks

### 8.1 Interface & Registration

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

Sinks receive `WideEventData` which contains pre-serialized JSON bytes (computed once, shared across all sinks) and the original `WideEvent` for sinks that need raw access.

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

### 8.2 Writing a Custom Sink

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

## 9. Sampling

### 9.1 Built-In: TailSampler

```csharp
builder.Services
    .AddVestige(options => { ... })
    .ConfigureSampling(sampling =>
    {
        sampling.AlwaysKeepErrors();
        sampling.AlwaysKeepSlowRequests(thresholdMs: 2000);
        sampling.AlwaysKeepWhen(e => e.Get<string>("user.subscription") == "enterprise");
        sampling.AlwaysKeepWhen(e => e.Has("feature_flags.new_checkout"));
        sampling.AlwaysKeepPaths("/api/checkout", "/api/payments/*");
        sampling.RateForPath("/healthz", 0.001);
        sampling.DefaultRate(0.05);
    });
```

### 9.2 Custom Strategy

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

// Registration:
builder.Services
    .AddVestige(options => { ... })
    .UseSamplingStrategy<AdaptiveSampler>();
```

### 9.3 Composable Chain

```csharp
.UseSamplingStrategy<AlwaysKeepErrorsSampler>()   // Keep or Defer
.UseSamplingStrategy<VipUserSampler>()             // Keep or Defer
.UseSamplingStrategy<RandomRateSampler>()          // Keep or Drop (terminal)
```

---

## 10. OpenTelemetry Integration (Optional Extension)

`Vestige.Extensions.OpenTelemetry` is an **optional package** that syncs `WideEvent` fields onto the current `Activity` (OTel span) as tags. This means teams with existing OTel pipelines get richly-tagged spans without changing their collector or backend.

### 10.1 How It Works

The extension registers an `IWideEventEnricher` that, on `OnBeforeEmit`, copies all `WideEvent` properties to `Activity.Current?.SetTag()`. It can also read existing `Activity` tags into the `WideEvent` at creation time.

```csharp
// Inside the extension:
public class ActivitySyncEnricher : WideEventEnricherBase
{
    private readonly ActivitySyncOptions _options;

    public ActivitySyncEnricher(IOptions<ActivitySyncOptions> options)
        => _options = options.Value;

    public override Task OnEventCreatedAsync(WideEvent ev, CancellationToken ct)
    {
        if (!_options.ReadExistingTags) return Task.CompletedTask;
        var activity = Activity.Current;
        if (activity is null) return Task.CompletedTask;

        // Import auto-instrumented tags from OTel into the WideEvent
        foreach (var tag in activity.Tags)
            ev.Set(tag.Key, tag.Value);

        return Task.CompletedTask;
    }

    public override Task OnBeforeEmitAsync(WideEvent ev, CancellationToken ct)
    {
        var activity = Activity.Current;
        if (activity is null) return Task.CompletedTask;

        // Sync all Vestige fields onto the OTel span
        foreach (var (key, value) in ev.Properties)
            activity.SetTag(key, value);

        foreach (var (key, ms) in ev.Timings)
            activity.SetTag($"{key}.duration_ms", ms);

        return Task.CompletedTask;
    }
}
```

### 10.2 Usage

```csharp
builder.Services
    .AddVestige(options => { ... })
    .AddHttpRequestEnricher()
    .AddConsoleSink()                           // primary output: Vestige JSON events
    .AddOpenTelemetrySync(o =>                  // optional: also enrich OTel spans
    {
        o.ReadExistingTags = true;              // import OTel auto-instrumented tags
        o.SyncToActivity = true;                // write Vestige fields to Activity
        o.ExcludePrefixes = ["internal."];       // don't sync internal fields
    });
```

### 10.3 What This Gives You

Without changing any OTel infrastructure, your spans now carry all the Vestige business context:

```
OTel span BEFORE Vestige:                OTel span AFTER Vestige:
├── http.method: POST                    ├── http.method: POST
├── http.route: /api/checkout            ├── http.route: /api/checkout
├── http.status_code: 200                ├── http.status_code: 200
└── duration: 1247ms                     ├── duration: 1247ms
                                         ├── user.id: user_789
                                         ├── user.subscription: premium
                                         ├── cart.total_cents: 16000
                                         ├── cart.item_count: 3
                                         ├── payment.provider: stripe
                                         ├── payment.attempt: 3
                                         ├── payment.charge.duration_ms: 1089
                                         ├── feature_flags.new_checkout: true
                                         └── error.code: card_declined
```

Your OTel backend (Jaeger, Honeycomb, Datadog, Tempo) now lets you query spans using Vestige's business fields — without any changes to your collector pipeline.

### 10.4 Important: Vestige Is Not an OTel Replacement

The OTel sync extension is purely additive. Vestige's primary job is emitting wide events to sinks. The Activity sync is a bonus for teams that also use OTel. You can use Vestige without OTel, OTel without Vestige, or both together.

---

## 11. Dependency Injection — No Statics

### 11.1 Accessing the Current Event

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

### 11.2 Background Jobs

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

---

## 12. ASP.NET Core Middleware

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

## 13. Extension Package Conventions

Every extension package follows the same pattern:

1. Reference only `Vestige` core (plus framework dependencies).
2. Provide an extension method on `VestigeBuilder`.
3. Use `IOptions<T>` for configuration.
4. Support constructor injection.
5. Return `VestigeBuilder` for chaining.
6. Follow naming: `Vestige.{Category}.{Name}`, method: `Add{Name}{Category}()` or `Add{Name}()`.

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

## 14. Testing

### 14.1 `Vestige.Testing` Package

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

// Fluent assertions helper
ev.Should().HaveField("cart.total_cents", 15999);
ev.Should().HaveTimingGreaterThan("payment.charge", TimeSpan.Zero);
ev.Should().HaveOutcome("success");
```

```csharp
// Unit test — no event scope, enrichment is silently ignored
var service = new CheckoutService(new NullWideEventAccessor(), mockPayments.Object);
await service.ProcessAsync(cart, user); // ev?.Set() is a no-op
```

```csharp
// Unit test — capture enrichment for assertions
var accessor = new TestWideEventAccessor();
var service = new CheckoutService(accessor, mockPayments.Object);
await service.ProcessAsync(cart, user);
Assert.Equal("premium", accessor.Current!.Get<string>("user.subscription"));
```

---

## 15. Solution & Repository Structure

```
vestige/
├── src/
│   ├── Vestige/                                  # Core abstractions + pipeline
│   ├── Vestige.Testing/                          # Test helpers
│   │
│   ├── Vestige.Extensions.AspNetCore/            # Middleware
│   ├── Vestige.Extensions.HttpClient/            # Outgoing HTTP handler
│   ├── Vestige.Extensions.EntityFrameworkCore/   # EF Core diagnostic listener
│   ├── Vestige.Extensions.Hosting/               # Background service support
│   ├── Vestige.Extensions.OpenTelemetry/         # Optional Activity sync
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
│   ├── Vestige.Samples.WebApi/                   # Minimal API example
│   ├── Vestige.Samples.Worker/                   # Background service example
│   └── Vestige.Samples.WithOpenTelemetry/        # OTel + Vestige together
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

## 16. Implementation Phases

### Phase 1 — Core + Console Sink + Essentials

**Packages**: `Vestige`, `Vestige.Testing`, `Vestige.Extensions.AspNetCore`, `Vestige.Extensions.Hosting`, `Vestige.Enrichers.HttpRequest`, `Vestige.Enrichers.HttpResponse`, `Vestige.Enrichers.Activity`, `Vestige.Enrichers.Environment`, `Vestige.Sinks.Console`.

**Milestone**: `dotnet add` a few packages, three lines of setup, rich wide events in the console.

### Phase 2 — Persistence Sinks

**Packages**: `Vestige.Sinks.File`, `Vestige.Sinks.Seq`, `Vestige.Sinks.Kafka`, `Vestige.Sinks.EventHubs`.

### Phase 3 — Framework Integrations & OTel

**Packages**: `Vestige.Extensions.HttpClient`, `Vestige.Extensions.EntityFrameworkCore`, `Vestige.Extensions.OpenTelemetry`, `Vestige.Enrichers.Identity`, `Vestige.Enrichers.Thread`.

### Phase 4 — Bridges & Polish

**Packages**: `Vestige.Bridges.Serilog`, `Vestige.Bridges.MicrosoftLogging`.

Plus: source-generated JSON serialization, object pooling, benchmarks (target < 5μs per request), documentation site, contributor guide.
