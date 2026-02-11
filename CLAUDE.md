# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Vestige** is a wide event logging library for .NET. Instead of emitting many low-context log lines per request, it accumulates one rich, structured event throughout the request lifecycle and emits it once at completion. This implements the "canonical log line" pattern (popularized by Stripe and Honeycomb).

The library is **not a replacement for `ILogger`** — it complements it, adding high-cardinality, high-dimensionality business context alongside existing logging.

## Build & Test Commands

Once the solution is scaffolded, the standard .NET CLI commands apply:

```bash
dotnet restore                        # Restore dependencies
dotnet build                          # Build entire solution
dotnet test                           # Run all tests
dotnet test --filter "FullyQualifiedName~ClassName"  # Run a single test class
dotnet pack --configuration Release   # Produce NuGet packages
```

## Architecture

### Core Concept: WideEvent

A `WideEvent` is a thread-safe accumulator (`ConcurrentDictionary`-backed) that collects fields throughout a request. It has:
- **Fixed fields**: `EventId`, `Timestamp`, `ServiceName`, `TraceId`, `SpanId`, `DurationMs`, `Outcome`
- **Dynamic property bag**: `Set()`, `Get<T>()`, `Has()`, `Remove()`
- **Sub-timers**: `Time(operationName)` returns `IDisposable` for auto-timing nested operations
- **Error capture**: `CaptureException(ex)` extracts structured error fields

### Pipeline Flow

```
Request → Middleware → Enrichers (OnEventCreatedAsync)
                → Application code calls ev.Set() / ev.Time()
                → Response → Enrichers (OnBeforeEmitAsync)
                → Tail sampling (Keep / Drop / Defer)
                → Serialize → Channel<WideEventData>
                → All registered IWideEventSink instances
                → (Optional) OTel Activity sync
```

Backpressure is handled by `System.Threading.Channels` with configurable `BufferCapacity`, `BatchSize`, `BatchFlushInterval`, and `OnBufferFull` behavior.

### Key Abstractions (in `Vestige` core package)

| Interface | Role |
|-----------|------|
| `IWideEventAccessor` | Inject into services to read `Current` (HTTP-scoped, nullable) |
| `IWideEventScopeFactory` | Create explicitly scoped events for background jobs |
| `IWideEventEnricher` | Add fields at event creation and before emit |
| `IWideEventSink` | Output destination (async, batching-capable) |
| `ISamplingStrategy` | Tail sampling: `Keep`, `Drop`, or `Defer` for chaining |
| `IWideEventSerializer` | Serialize `WideEvent` → `WideEventData` (JSON bytes) |
| `IWideEventPipeline` | Internal channel queue + dispatch |

### Package Ecosystem

Follows a Serilog-style modular pattern — tiny core, opt-in packages:

- **`Vestige`** — core abstractions, pipeline, DI wiring
- **`Vestige.Testing`** — `InMemorySink`, `TestWideEventAccessor`, assertion helpers
- **`Vestige.Extensions.AspNetCore`** — middleware (`UseVestige()`)
- **`Vestige.Extensions.Hosting`** — `IWideEventScopeFactory` for background services
- **`Vestige.Extensions.HttpClient`** — `DelegatingHandler` for outgoing HTTP calls
- **`Vestige.Extensions.EntityFrameworkCore`** — EF Core diagnostic listener
- **`Vestige.Extensions.OpenTelemetry`** — sync WideEvent fields onto `Activity.Current` tags
- **`Vestige.Enrichers.*`** — `HttpRequest`, `HttpResponse`, `Identity`, `Activity`, `Environment`, `Thread`
- **`Vestige.Sinks.*`** — `Console`, `File`, `Seq`, `Kafka`, `EventHubs`, `RabbitMQ`
- **`Vestige.Bridges.*`** — `Serilog`, `MicrosoftLogging` (forward log events as WideEvent fields)

### Extension Pattern (all packages follow this)

Every enricher and sink package exposes extension methods on `VestigeBuilder` returning `VestigeBuilder` for chaining:

```csharp
public static VestigeBuilder AddMyEnricher(this VestigeBuilder builder)
{
    builder.Services.AddSingleton<IWideEventEnricher, MyEnricher>();
    return builder;
}
```

DI setup chains like:
```csharp
builder.Services
    .AddVestige(o => { o.ServiceName = "my-service"; })
    .AddHttpRequestEnricher()
    .AddConsoleSink();
app.UseVestige();
```

### Tail Sampling

Sampling decisions are made **after** the request completes, so strategies have access to all accumulated business fields:

```csharp
.ConfigureSampling(s =>
{
    s.AlwaysKeepErrors();
    s.AlwaysKeepSlowRequests(thresholdMs: 2000);
    s.AlwaysKeepWhen(e => e.Get<string>("user.subscription") == "enterprise");
    s.DefaultRate(0.05);
});
```

Strategies chain: each returns `Keep`, `Drop`, or `Defer` (pass to next strategy).

### Background Job Pattern

```csharp
await using var scope = _vestige.BeginScope("job.process_order");
var ev = scope.Event;  // Always non-null (unlike IWideEventAccessor.Current)
// ... set fields, do work ...
ev.Outcome = "success";
// Event auto-emitted on scope dispose
```

## Implementation Phases

1. **Phase 1** (MVP): `Vestige` core, `Vestige.Testing`, `Vestige.Extensions.AspNetCore`, `Vestige.Extensions.Hosting`, `HttpRequest`/`HttpResponse`/`Environment` enrichers, `ConsoleSink`
2. **Phase 2**: `File`, `Seq`, `Kafka`, `EventHubs` sinks
3. **Phase 3**: `HttpClient`, `EntityFrameworkCore` extensions; `Identity`/`Thread` enrichers; `OpenTelemetry` sync
4. **Phase 4**: `Serilog`/`MicrosoftLogging` bridges; source-generated JSON serialization; object pooling; benchmarks (target < 5μs per-request overhead)

## Key Documents

- **`README.md`** — Public-facing documentation with quick start, usage examples, and package overview
- **`vestige-architecture-plan.md`** — Detailed internal design: all interfaces, pipeline mechanics, serialization strategy, configuration shapes, and implementation guidance per phase
