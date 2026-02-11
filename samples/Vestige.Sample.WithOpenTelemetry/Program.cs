// =============================================================================
//  Vestige + OpenTelemetry — bidirectional sync sample
// =============================================================================
//
//  This sample shows how Vestige.Extensions.OpenTelemetry bridges the gap
//  between wide events and OTel traces:
//
//  ┌─ READS IN ──────────────────────────────────────────────────────────────┐
//  │  OTel auto-instruments incoming HTTP requests and outgoing HttpClient   │
//  │  calls.  At event creation the OpenTelemetrySyncEnricher copies those   │
//  │  Activity tags into the wide event — no extra code needed.              │
//  └─────────────────────────────────────────────────────────────────────────┘
//
//  ┌─ WRITES OUT ────────────────────────────────────────────────────────────┐
//  │  Before the wide event is emitted, the sync enricher copies all Vestige │
//  │  fields (user, cart, payment, …) onto Activity.Current as span tags.    │
//  │  Your OTel backend (Jaeger / Honeycomb / Grafana Tempo) will show rich  │
//  │  business context on every trace — zero extra instrumentation.          │
//  └─────────────────────────────────────────────────────────────────────────┘
//
//  Console output interleaves both:
//   • [Vestige]  — one JSON wide event per request, containing OTel fields
//   • [OTel]     — one span per request, containing Vestige business fields
// =============================================================================

using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vestige;
using Vestige.Enrichers.Activity;
using Vestige.Enrichers.Environment;
using Vestige.Enrichers.HttpRequest;
using Vestige.Enrichers.HttpResponse;
using Vestige.Extensions.AspNetCore;
using Vestige.Extensions.Hosting;
using Vestige.Extensions.OpenTelemetry;
using Vestige.Sinks.Console;

var builder = WebApplication.CreateBuilder(args);

// ── 1. OpenTelemetry SDK ───────────────────────────────────────────────────
//
//  Standard OTel setup: auto-instrument ASP.NET Core + HttpClient, then
//  export traces to the console so we can see the span tags side-by-side
//  with the Vestige wide events.
//
//  In production you'd swap AddConsoleExporter() for AddOtlpExporter() to
//  ship to Jaeger, Grafana Tempo, Honeycomb, Datadog, etc.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService("otel-sample", serviceVersion: "1.0.0"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()   // auto-tags incoming HTTP spans
        .AddHttpClientInstrumentation()   // auto-tags outgoing HttpClient spans
        .AddConsoleExporter());           // print spans to stdout for demo

// ── 2. Vestige setup ───────────────────────────────────────────────────────
builder.Services
    .AddVestige(o =>
    {
        o.ServiceName    = "otel-sample";
        o.ServiceVersion = "1.0.0";
        o.Environment    = builder.Environment.EnvironmentName;
    })
    .AddEnvironmentEnricher()
    .AddActivityEnricher()     // stamps trace_id / span_id from Activity
    .AddHttpRequestEnricher()
    .AddHttpResponseEnricher()
    // ── The bridge ─────────────────────────────────────────────────────────
    //  ReadActivityTags  = true  → OTel auto-instrumented Activity tags flow
    //                             INTO the wide event at event creation.
    //  WriteToActivity   = true  → All Vestige fields flow OUT onto the span
    //                             before the wide event is emitted.
    .AddOpenTelemetrySync(o =>
    {
        o.ReadActivityTags = true;
        o.WriteToActivity  = true;
        // Keep internal-only fields off the OTel spans to avoid noise.
        o.ExcludePrefixes  = ["internal."];
    })
    .AddConsoleSink(o => o.Indented = builder.Environment.IsDevelopment())
    .AddHostingSupport()
    .ConfigureSampling(s => s.DefaultRate(1.0));

// ── 3. HttpClient for outgoing-call demo ──────────────────────────────────
//  OTel's HttpClient instrumentation will auto-create a child span for every
//  outgoing call; that span's tags are then imported into the wide event.
builder.Services.AddHttpClient("external");

var app = builder.Build();
app.UseVestige();

// ── Endpoints ─────────────────────────────────────────────────────────────

// GET /checkout
//  Demonstrates the full bidirectional sync:
//
//  READS IN  – by the time this handler runs, the wide event already contains
//               OTel auto-instrumented fields from the incoming request span
//               (http.request.method, url.path, network.protocol.version, …).
//
//  WRITES OUT – the ev.Set() calls below add business context; before the
//               event is emitted those fields are copied to the OTel span.
//               The OTel console exporter will print them as span attributes.
app.MapGet("/checkout", async (
    [FromServices] IWideEventAccessor ev,
    [FromServices] IHttpClientFactory factory) =>
{
    // ── Business context ─────────────────────────────────────────────
    ev.Current
      ?.Set("user.id",           "user_789")
       .Set("user.subscription", "premium")
       .Set("cart.id",           "cart_abc")
       .Set("cart.total_cents",  16_000)
       .Set("cart.item_count",   3)
       .Set("feature_flags.new_checkout", true);

    // ── Simulate payment processing ──────────────────────────────────
    using var payment = ev.Current?.Scope("payment.charge");
    await Task.Delay(25); // simulate gateway latency
    payment?.Set("attempts", 1);

    ev.Current
      ?.Set("payment.provider", "stripe")
       .Set("payment.attempt",  1);

    // ── Outgoing HTTP call ────────────────────────────────────────────
    //  OTel auto-instruments this call and creates a child span.
    //  The sync enricher will pull that span's tags (http.request.method,
    //  server.address, http.response.status_code, …) into the wide event.
    var client = factory.CreateClient("external");
    using var resp = await client.GetAsync("https://httpbin.org/get");

    ev.Current?.Set("external.status", (int)resp.StatusCode);

    return Results.Ok(new
    {
        orderId  = 42,
        message  = "Wide event contains OTel infra fields; " +
                   "OTel span contains Vestige business fields."
    });
});

// GET /orders — lightweight endpoint; still produces a full wide event
app.MapGet("/orders", ([FromServices] IWideEventAccessor ev) =>
{
    ev.Current
      ?.Set("orders.filter", "active")
       .Set("orders.page",   1);

    var orders = new[]
    {
        new { Id = 1, Product = "Widget A", Total = 29.99 },
        new { Id = 2, Product = "Widget B", Total = 49.99 },
    };

    ev.Current?.Set("orders.count", orders.Length);
    return Results.Ok(orders);
});

// GET /error — forces an error to demonstrate outcome / error fields
app.MapGet("/error", ([FromServices] IWideEventAccessor ev) =>
{
    ev.Current?.Set("demo.trigger", "forced-error");
    throw new InvalidOperationException("Demo error — check the wide event error fields.");
});

app.Run();
