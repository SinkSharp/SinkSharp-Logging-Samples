// ═══════════════════════════════════════════════════════════════════════════════
// SinkSharp — Minimal API Sample
// ═══════════════════════════════════════════════════════════════════════════════
// Demonstrates:
//   ✅ Minimal API (no controllers) with SinkSharp
//   ✅ Per-category log levels (silence Microsoft/System noise)
//   ✅ Per-sink minimum level — console=Debug, file=Warning, dashboard=Information
//   ✅ Correlation ID middleware — reads from header or generates new
//   ✅ PII masking — email/card/SSN masked before any sink writes
//   ✅ Dashboard mounted at /sinksharp/dashboard (same port as API)
//   ✅ Health check at /health
//   ✅ Structured logging with typed properties
//   ✅ Error logging with exception details
//
// Run:  dotnet run
// Open: https://localhost:5001/sinksharp/dashboard
//       https://localhost:5001/health
//       https://localhost:5001/products
//       https://localhost:5001/checkout    (GET — simulates POST for easy browser testing)
// ═══════════════════════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.File;
using SinkSharp.Core.Middleware;

var builder = WebApplication.CreateBuilder(args);
var appName = builder.Environment.ApplicationName;
var env     = builder.Environment.EnvironmentName;

// ── 1. SinkSharp with health check ───────────────────────────────────────────
DashboardSink dashSink = null!;

builder.Services.AddSinkSharpWithHealth(options => options

    // Global minimum level
    .MinimumLevel(LogLevel.Debug)

    // Per-category overrides — silence framework noise in production
    .MinimumLevelFor("Microsoft",                     LogLevel.Warning)
    .MinimumLevelFor("Microsoft.AspNetCore.Routing",  LogLevel.Error)
    .MinimumLevelFor("System.Net.Http",               LogLevel.Error)

    // Enrichers — add metadata to every log event
    .WithCorrelationId()                // auto-propagate via AsyncLocal
    .WithEnvironment(appName, env)      // AppName + Environment + MachineName
    .WithPiiMasking()                   // mask email, card, SSN, phone

    // Dashboard — same port as API; Information+ keeps Debug noise out of the UI
    .WithDashboard(out dashSink, minLevel: LogLevel.Information)

    // Sinks — per-sink minimum levels: each destination gets the right verbosity
    //   Console  : Debug+        — full detail in your terminal / CI output
    //   File     : Warning+      — production files stay small and actionable
    //
    // Global minimum (Debug) must be ≤ any per-sink minimum.
    // The global gate fires first (in SinkSharpLogger.IsEnabled); events that
    // pass it then hit each sink's own level guard in the pipeline.
    .WriteTo(new ColoredConsoleSink(),         LogLevel.Debug)
    .WriteTo(new RollingFileSink("logs", appName), LogLevel.Warning));

builder.Services.AddSinkSharpDashboard(dashSink);
    // ── Multi-pod option — uncomment when running 2+ Kubernetes replicas ─────────
    // Requires: dotnet add package Microsoft.AspNetCore.SignalR.StackExchangeRedis
    // builder.Services.AddSinkSharpDashboardRedis(dashSink, signalR =>
    //     signalR.AddStackExchangeRedis(
    //         builder.Configuration["Redis:ConnectionString"]!,
    //         options => options.Configuration.ChannelPrefix =
    //             StackExchange.Redis.RedisChannel.Literal("MyApp.SinkSharp")));

var app = builder.Build();

// ── Correlation middleware ──────────────────────────────────────────────────
app.UseSinkSharp();

// ── 3. Dashboard + health check ───────────────────────────────────────────────
app.UseSinkSharpDashboard("/sinksharp/dashboard");
app.MapHealthChecks("/health");

// ── 4. API endpoints ──────────────────────────────────────────────────────────
app.MapGet("/", () => new
{
    app       = appName,
    env       = env,
    dashboard = "/sinksharp/dashboard",
    health    = "/health",
    endpoints = new[] { "/products", "/products/{id}", "/checkout", "/fail" }
});

app.MapGet("/products", (ILogger<Program> log) =>
{
    // Structured: {Count} is a typed int property, not a string concat
    var products = new[]
    {
        new { Id = 1, Name = "Widget Pro",  Price = 49.99m,  Stock = 142 },
        new { Id = 2, Name = "Gadget Plus", Price = 129.99m, Stock = 7   },
        new { Id = 3, Name = "Tool Kit",    Price = 24.99m,  Stock = 0   },
    };
    log.LogInformation("Product catalogue fetched — {Count} items", products.Length);
    return Results.Ok(products);
});

app.MapGet("/products/{id:int}", (int id, ILogger<Program> log) =>
{
    log.LogDebug("Fetching product {ProductId}", id);

    if (id == 3)
    {
        log.LogWarning("Product {ProductId} is out of stock", id);
        return Results.Ok(new { Id = id, Name = "Tool Kit", Price = 24.99m, Stock = 0 });
    }
    if (id > 100)
    {
        log.LogWarning("Product {ProductId} not found", id);
        return Results.NotFound(new { error = $"Product {id} not found" });
    }
    return Results.Ok(new { Id = id, Name = "Widget Pro", Price = 49.99m, Stock = 142 });
});

app.MapGet("/checkout", (ILogger<Program> log) =>
{
    // PII masking in action — email and card number masked before reaching any sink
    log.LogInformation(
        "Checkout initiated for {Email} — card {CardNumber}",
        "customer@example.com",   // → masked: ***@***.***
        "4111-1111-1111-1111");    // → masked: ****-****-****-****

    log.LogInformation("Payment processed — OrderId {OrderId}, Amount {Amount:C}",
        Guid.NewGuid().ToString("N")[..8], 179.98m);

    return Results.Ok(new { orderId = Guid.NewGuid(), status = "Confirmed" });
});

app.MapGet("/fail", (ILogger<Program> log) =>
{
    // Exception logging — stack trace visible in dashboard detail pane
    try
    {
        throw new InvalidOperationException("Downstream payment service timeout");
    }
    catch (Exception ex)
    {
        // Error level → unbounded channel — never dropped even under heavy load
        log.LogError(ex, "Payment gateway failure on endpoint {Endpoint}", "/checkout");
    }
    return Results.Problem("Payment service unavailable", statusCode: 503);
});

app.Run();
