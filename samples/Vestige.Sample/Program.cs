using Microsoft.AspNetCore.Mvc;
using Vestige;
using Vestige.Enrichers.Activity;
using Vestige.Enrichers.Environment;
using Vestige.Enrichers.HttpRequest;
using Vestige.Enrichers.HttpResponse;
using Vestige.Extensions.AspNetCore;
using Vestige.Extensions.Hosting;
using Vestige.Sinks.Console;

var builder = WebApplication.CreateBuilder(args);

// ── Vestige setup ─────────────────────────────────────────────────────────────
builder.Services
    .AddVestige(o =>
    {
        o.ServiceName    = "vestige-sample";
        o.ServiceVersion = "1.0.0";
        o.Environment    = builder.Environment.EnvironmentName;
    })
    .AddEnvironmentEnricher()
    .AddActivityEnricher()
    .AddHttpRequestEnricher()
    .AddHttpResponseEnricher()
    .AddConsoleSink(o => o.Indented = builder.Environment.IsDevelopment())
    .AddHostingSupport()
    .ConfigureSampling(s =>
    {
        s.AlwaysKeepErrors();
        s.AlwaysKeepSlowRequests(thresholdMs: 1000);
        s.DefaultRate(1.0); // keep everything in the sample
    })
    .ConfigurePipeline(pipeline =>
    {
        pipeline.BufferCapacity = 10_000;
        pipeline.BatchSize = 1;
    });;

builder.Services.AddHostedService<OrderProcessingJob>();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// ── Vestige middleware — place early so it wraps all endpoints ─────────────────
app.UseVestige();

// ── Endpoints ─────────────────────────────────────────────────────────────────

// GET /orders — list orders, stamp business context onto the wide event
app.MapGet("/orders", ([FromServices] IWideEventAccessor ev) =>
{
    ev.Current?.Set("orders.filter", "all")
               .Set("orders.page", 1);

    var orders = new[]
    {
        new { Id = 1, Product = "Widget A", Total = 29.99 },
        new { Id = 2, Product = "Widget B", Total = 49.99 },
    };

    ev.Current?.Set("orders.count", orders.Length);
    return Results.Ok(orders);
});

// POST /orders — create order, stamp more context
app.MapPost("/orders", ([FromBody] CreateOrderRequest req, [FromServices] IWideEventAccessor ev) =>
{
    ev.Current?.Set("order.product", req.Product)
               .Set("order.quantity", req.Quantity)
               .Set("customer.id", req.CustomerId);

    // Demonstrate sub-scope (fields + duration stamped under "db.insert.*")
    using (ev.Current?.Scope("db.insert")) { Thread.Sleep(10); }

    var order = new { Id = 42, req.Product, req.Quantity, req.CustomerId };
    ev.Current?.Set("order.id", order.Id);

    return Results.Created($"/orders/{order.Id}", order);
});

// GET /orders/{id} — fetch a single order
app.MapGet("/orders/{id:int}", (int id, [FromServices] IWideEventAccessor ev) =>
{
    ev.Current?.Set("order.id", id);

    if (id != 42)
    {
        ev.Current?.Set("order.found", false);
        return Results.NotFound();
    }

    ev.Current?.Set("order.found", true);
    return Results.Ok(new { Id = 42, Product = "Widget A", Quantity = 1 });
});

// GET /error — intentionally throws to demonstrate error capture
app.MapGet("/error", ([FromServices] IWideEventAccessor ev) =>
{
    ev.Current?.Set("demo.trigger", "forced-error");
    throw new InvalidOperationException("This is a demo error");
});

app.Run();

// ── Supporting types ──────────────────────────────────────────────────────────

record CreateOrderRequest(string Product, int Quantity, string CustomerId);

// Background job demonstrating IWideEventScopeFactory usage
sealed class OrderProcessingJob(IWideEventScopeFactory vestige, ILogger<OrderProcessingJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(500, stoppingToken); // wait for app to start

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessBatchAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = vestige.BeginScope("job.process_orders");
        var ev = scope.Event;

        try
        {
            ev.Set("job.batch_size", 10);

            using (ev.Scope("db.fetch"))
            {
                await Task.Delay(5, cancellationToken); // simulate DB read
            }

            ev.Set("job.processed", 10);
            ev.Outcome = "success";

            logger.LogInformation("Processed order batch.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            ev.CaptureException(ex);
        }
    }
}
