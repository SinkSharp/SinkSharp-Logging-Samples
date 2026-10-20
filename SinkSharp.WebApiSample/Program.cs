// SinkSharp — Web API / Minimal API Sample
// Dashboard mounts on the same host and port as your API
// Run: dotnet run
// Open: https://localhost:5001/sinksharp/dashboard

using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.File;
using SinkSharp.Core.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ── SinkSharp setup ───────────────────────────────────────────
DashboardSink dashSink = null!;

builder.Services.AddSinkSharpWithHealth(options => options
    .MinimumLevel(LogLevel.Information)
    .MinimumLevelFor("Microsoft", LogLevel.Warning)
    .MinimumLevelFor("System.Net", LogLevel.Error)
    .WithCorrelationId()
    .WithEnvironment(builder.Environment.ApplicationName,
                     builder.Environment.EnvironmentName)
    .WithMachineName()   // standalone machine/pod name (also in WithEnvironment above — shows the API)
    .WithPiiMasking()
    // Drop health check noise — never reaches any sink
    .Filter(e => !e.MessageTemplate.Contains("health", StringComparison.OrdinalIgnoreCase))
    // Dashboard: Information+ (default — no extra filter needed here)
    .WithDashboard(out dashSink)
    // Console: global minimum (Information+)
    .WriteTo(new ColoredConsoleSink())
    // File: Warning+ only — keeps production log files small and actionable
    .WriteTo(new RollingFileSink("logs", "SinkSharp.WebApi"), LogLevel.Warning));

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

// ── Mount dashboard — same host, same port ────────────────────
// https://localhost:5001/sinksharp/dashboard       ← UI
// https://localhost:5001/sinksharp/dashboard/hub   ← SignalR
// https://localhost:5001/sinksharp/dashboard/stats ← JSON stats
app.UseSinkSharpDashboard("/sinksharp/dashboard");

// ── Health check ──────────────────────────────────────────────
// GET /health → { "status": "Healthy", "results": { "sinksharp": ... } }
app.MapHealthChecks("/health");

// ── Sample API endpoints ──────────────────────────────────────
app.MapGet("/", () => new {
    app     = "SinkSharp Web API Sample",
    docs    = "/sinksharp/dashboard",
    health  = "/health",
    orders  = "/orders",
});

app.MapGet("/orders", (ILogger<Program> logger) =>
{
    logger.LogInformation("Listing all orders");
    return Results.Ok(new[] {
        new { Id = 1, Status = "Shipped",    Amount = 149.99m },
        new { Id = 2, Status = "Processing", Amount = 89.50m  },
        new { Id = 3, Status = "Pending",    Amount = 299.00m },
    });
});

app.MapGet("/orders/{id:int}", (int id, ILogger<Program> logger) =>
{
    logger.LogInformation("Fetching order {OrderId}", id);
    if (id > 100)
    {
        logger.LogWarning("Order {OrderId} not found", id);
        return Results.NotFound(new { error = $"Order {id} not found" });
    }
    return Results.Ok(new { Id = id, Status = "Processing", Amount = 99.99m });
});

app.MapGet("/orders/new", (ILogger<Program> logger) =>
{
    logger.LogInformation("Creating new order for user {Email}", "buyer@example.com");
    // PII masked in log → "buyer@***"
    logger.LogWarning("Stock low for item {ItemId}, threshold {Threshold}", "SKU-001", 5);
    return Results.Created("/orders/42", new { Id = 42, Status = "Pending" });
});

app.MapGet("/orders/{id:int}/cancel", (int id, ILogger<Program> logger) =>
{
    logger.LogError("Failed to cancel order {OrderId} — payment already captured", id);
    return Results.Problem("Cannot cancel a captured order");
});

app.Run();
