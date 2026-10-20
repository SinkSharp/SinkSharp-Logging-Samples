// ═══════════════════════════════════════════════════════════════════════════════
// SinkSharp — Scoped Logging & Correlation Sample
// ═══════════════════════════════════════════════════════════════════════════════
// Demonstrates:
//   ✅ Correlation ID — automatic propagation through async call chains
//   ✅ BeginScope() — MEL standard scopes work with SinkSharp
//   ✅ CorrelationContext.Use() — explicit scope with IDisposable
//   ✅ Nested scopes — inner scope overrides outer for that property
//   ✅ Async propagation — correlation flows correctly across await boundaries
//   ✅ Parallel tasks — each task gets its own correlation context
//   ✅ How to trace a full request across multiple services
//   ✅ Filtering dashboard by correlation ID to trace one request
//
// Run:  dotnet run
// Open: https://localhost:5001/sinksharp/dashboard
//       Click a correlation ID in the dashboard to filter all events for that request
// ═══════════════════════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.File;
using SinkSharp.Core.Middleware;

var builder = WebApplication.CreateBuilder(args);

DashboardSink dashSink = null!;

builder.Services.AddSinkSharp(options => options
    .MinimumLevel(LogLevel.Debug)
    .MinimumLevelFor("Microsoft", LogLevel.Warning)
    .WithCorrelationId()   // ← enables AsyncLocal correlation propagation
    .WithEnvironment(builder.Environment.ApplicationName, builder.Environment.EnvironmentName)
    .WithPiiMasking()
    .WithDashboard(out dashSink)
    .WriteTo(new ColoredConsoleSink(showFullCategory: true))
    .WriteTo(new RollingFileSink("logs", "scoped-sample")));

builder.Services.AddSinkSharpDashboard(dashSink);
    // ── Multi-pod option — uncomment when running 2+ Kubernetes replicas ─────────
    // Requires: dotnet add package Microsoft.AspNetCore.SignalR.StackExchangeRedis
    // builder.Services.AddSinkSharpDashboardRedis(dashSink, signalR =>
    //     signalR.AddStackExchangeRedis(
    //         builder.Configuration["Redis:ConnectionString"]!,
    //         options => options.Configuration.ChannelPrefix =
    //             StackExchange.Redis.RedisChannel.Literal("MyApp.SinkSharp")));

// Register services that use ILogger<T> — correlation flows through all of them
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<PaymentService>();

var app = builder.Build();

// ── Correlation middleware — set ONCE per request, flows everywhere ────────────
// app.UseSinkSharp() reads X-Correlation-Id from the request header (or generates
// a new 12-char GUID), sets CorrelationContext.Current via AsyncLocal so it flows
// through all awaits, and writes X-Correlation-Id to the response header.
// All ILogger<T> calls in OrderService, InventoryService, PaymentService
// automatically include this correlation ID without any manual passing.
app.UseSinkSharp();

app.UseSinkSharpDashboard("/sinksharp/dashboard");

app.MapGet("/", () => new
{
    sample  = "Scoped Logging & Correlation Sample",
    tip     = "Click a Correlation ID in the dashboard to filter that request's trace",
    routes  = new[]
    {
        "GET /order/{id} — traces through 3 services with one correlation ID",
        "GET /parallel   — shows correlation in parallel async tasks",
        "GET /nested     — shows nested scope overriding",
    }
});

// ── Full request trace through multiple services ──────────────────────────────
// One correlation ID — all three services log under the same ID automatically
app.MapGet("/order/{id:int}", async (int id, OrderService orders, HttpContext ctx) =>
{
    var result = await orders.ProcessAsync(id);
    return Results.Ok(result);
});

// ── Parallel tasks — each gets independent correlation context ────────────────
app.MapGet("/parallel", async (ILogger<Program> log) =>
{
    log.LogInformation("Starting parallel processing");

    // Each Task.Run creates a copy of the current AsyncLocal context
    // So parallel tasks share the outer correlation ID
    var tasks = Enumerable.Range(1, 5).Select(i => Task.Run(async () =>
    {
        // Override with a sub-task correlation in each parallel branch
        using var _ = CorrelationContext.Use($"sub-{i:D3}");
        log.LogInformation("Parallel task {TaskId} started", i);
        await Task.Delay(Random.Shared.Next(50, 200));
        log.LogInformation("Parallel task {TaskId} completed", i);
        return i;
    }));

    var results = await Task.WhenAll(tasks);
    return Results.Ok(new { completed = results });
});

// ── Nested scopes — inner overrides outer ─────────────────────────────────────
app.MapGet("/nested", async (ILogger<Program> log) =>
{
    log.LogInformation("Outer scope — request level");

    using (CorrelationContext.Use("outer-001"))
    {
        log.LogInformation("Inside outer correlation scope");

        // Nested scope overrides for this block only
        using (CorrelationContext.Use("inner-001"))
        {
            log.LogInformation("Inside inner correlation — overrides outer");
            await Task.Delay(50);
            log.LogInformation("Still in inner scope after await");
        }

        // Back to outer scope
        log.LogInformation("Back in outer scope after inner disposed");
    }

    log.LogInformation("Back to request-level correlation");
    return Results.Ok(new { message = "Check dashboard — filter by outer-001 and inner-001" });
});

app.Run();

// ── Service classes — all use ILogger<T>, correlation flows automatically ─────

public sealed class OrderService(
    ILogger<OrderService> log,
    InventoryService inventory,
    PaymentService payment)
{
    public async Task<object> ProcessAsync(int orderId)
    {
        // No correlation ID passed — it flows via AsyncLocal automatically
        log.LogInformation("Order {OrderId} — processing started", orderId);

        var inStock = await inventory.CheckStockAsync(orderId);
        if (!inStock)
        {
            log.LogWarning("Order {OrderId} — insufficient stock", orderId);
            return new { OrderId = orderId, Status = "Rejected", Reason = "OutOfStock" };
        }

        var paid = await payment.ChargeAsync(orderId, 99.99m);
        if (!paid)
        {
            log.LogError("Order {OrderId} — payment failed", orderId);
            return new { OrderId = orderId, Status = "Failed", Reason = "PaymentDeclined" };
        }

        log.LogInformation("Order {OrderId} — processing complete", orderId);
        return new { OrderId = orderId, Status = "Confirmed", Amount = 99.99m };
    }
}

public sealed class InventoryService(ILogger<InventoryService> log)
{
    public async Task<bool> CheckStockAsync(int orderId)
    {
        log.LogDebug("Checking stock for order {OrderId}", orderId);
        await Task.Delay(30); // simulate DB call
        var inStock = orderId % 5 != 0; // simulate occasional out-of-stock
        log.LogInformation("Stock check for order {OrderId} — {Result}",
            orderId, inStock ? "available" : "insufficient");
        return inStock;
    }
}

public sealed class PaymentService(ILogger<PaymentService> log)
{
    public async Task<bool> ChargeAsync(int orderId, decimal amount)
    {
        log.LogDebug("Initiating payment for order {OrderId} — amount {Amount:C}",
            orderId, amount);
        await Task.Delay(50); // simulate payment gateway call
        var success = orderId % 7 != 0; // simulate occasional declines
        if (success)
            log.LogInformation("Payment captured for order {OrderId} — {Amount:C}",
                orderId, amount);
        else
            log.LogWarning("Payment declined for order {OrderId}", orderId);
        return success;
    }
}
