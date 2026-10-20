// ═══════════════════════════════════════════════════════════════════════════════
// SinkSharp — Azure Deployment Sample
// ═══════════════════════════════════════════════════════════════════════════════
//
// HOW IT WORKS:
//   AzureContextEnricher reads Azure environment variables ONCE at app startup.
//   After that, zero cost — values are cached in memory.
//   Every log event automatically gets Azure context properties stamped on it.
//
// HOW TO RUN LOCALLY:
//   dotnet run
//   Open: https://localhost:5001/sinksharp/dashboard
//   Locally, Azure properties will be empty (no Azure env vars set).
//   You can simulate them:
//     $env:WEBSITE_SITE_NAME    = "my-api"
//     $env:REGION_NAME          = "East US"
//     $env:WEBSITE_SLOT_NAME    = "staging"
//     $env:WEBSITE_RESOURCE_GROUP = "my-rg"
//     dotnet run
//
// HOW TO DEPLOY:
//   Works on ANY Azure compute — no config needed.
//   Azure sets the env vars automatically when you deploy.
//
// SUPPORTED AZURE SERVICES:
//
//   ┌──────────────────────────────┬──────────────────────────────────────────┐
//   │ Service                      │ Properties added automatically           │
//   ├──────────────────────────────┼──────────────────────────────────────────┤
//   │ App Service                  │ AzureSiteName, AzureRegion,              │
//   │ (Web App, Function App,      │ AzureSlot, AzureResourceGroup            │
//   │  App Service on Linux)       │                                          │
//   ├──────────────────────────────┼──────────────────────────────────────────┤
//   │ Azure Kubernetes Service     │ K8sPodName, K8sNamespace                 │
//   │ (AKS)                        │ (set in your Deployment yaml)            │
//   ├──────────────────────────────┼──────────────────────────────────────────┤
//   │ Azure Container Apps         │ K8sPodName, K8sNamespace                 │
//   │                              │ (set in your container env vars)         │
//   ├──────────────────────────────┼──────────────────────────────────────────┤
//   │ Azure Container Instances    │ AzureSiteName (if set manually)          │
//   │                              │ Add your own env vars as needed          │
//   ├──────────────────────────────┼──────────────────────────────────────────┤
//   │ Azure Functions              │ AzureSiteName, AzureRegion,              │
//   │                              │ AzureResourceGroup                       │
//   └──────────────────────────────┴──────────────────────────────────────────┘
//
// ENV VARS READ (all optional — missing ones are simply omitted):
//
//   WEBSITE_SITE_NAME       → AzureSiteName      (App Service, Functions)
//   WEBSITE_SLOT_NAME       → AzureSlot           (deployment slot: prod/staging)
//   REGION_NAME             → AzureRegion         (East US, West Europe, etc.)
//   WEBSITE_RESOURCE_GROUP  → AzureResourceGroup  (your resource group name)
//   POD_NAME                → K8sPodName          (AKS pod — set in Deployment yaml)
//   POD_NAMESPACE           → K8sNamespace        (AKS namespace)
//
// AKS DEPLOYMENT YAML (add to your container spec to get K8s properties):
//
//   env:
//     - name: POD_NAME
//       valueFrom:
//         fieldRef:
//           fieldPath: metadata.name
//     - name: POD_NAMESPACE
//       valueFrom:
//         fieldRef:
//           fieldPath: metadata.namespace
//
// WHAT YOU SEE IN LOGS (example — App Service staging slot in East US):
//
//   {
//     "timestamp": "2026-10-07T10:30:00Z",
//     "level": "Information",
//     "message": "Order 42 placed",
//     "correlationId": "a1b2c3d4",
//     "appName": "OrderApi",
//     "environment": "Production",
//     "azureSiteName": "order-api",
//     "azureSlot": "staging",
//     "azureRegion": "East US",
//     "azureResourceGroup": "order-api-rg",
//     "orderId": 42
//   }
//
// ═══════════════════════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Enrichers.Azure;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.File;
using SinkSharp.Core.Middleware;

var builder = WebApplication.CreateBuilder(args);
var appName = builder.Environment.ApplicationName;
var env     = builder.Environment.EnvironmentName;

// ── 1. SinkSharp with Azure enricher ─────────────────────────────────────────
DashboardSink dashSink = null!;

builder.Services.AddSinkSharp(options => options
    .MinimumLevel(LogLevel.Information)
    .MinimumLevelFor("Microsoft",  LogLevel.Warning)  // silence ASP.NET noise
    .MinimumLevelFor("System.Net", LogLevel.Error)
    .WithCorrelationId()                              // auto-propagate via AsyncLocal
    .WithEnvironment(appName, env)                    // AppName + Environment + MachineName
    .WithPiiMasking()                                 // mask emails, cards, SSNs, phones
    .WithAzureContext()                               // ← Azure: region, slot, RG, pod
    .WithDashboard(out dashSink)
    .WriteTo(new ColoredConsoleSink())
    .WriteTo(new RollingFileSink("logs", appName)));

// Health check is OPTIONAL — use AddSinkSharp() without health,
// or AddSinkSharpWithHealth() to expose /health endpoint.
// This sample intentionally uses AddSinkSharp() to show it's not mandatory.

// ── 2. Register dashboard ─────────────────────────────────────────────────────
builder.Services.AddSinkSharpDashboard(dashSink);
    // ── Multi-pod option — uncomment when running 2+ Kubernetes replicas ─────────
    // Requires: dotnet add package Microsoft.AspNetCore.SignalR.StackExchangeRedis
    // builder.Services.AddSinkSharpDashboardRedis(dashSink, signalR =>
    //     signalR.AddStackExchangeRedis(
    //         builder.Configuration["Redis:ConnectionString"]!,
    //         options => options.Configuration.ChannelPrefix =
    //             StackExchange.Redis.RedisChannel.Literal("MyApp.SinkSharp")));

var app = builder.Build();

// ── 3. Correlation ID middleware ──────────────────────────────────────────────
// Reads X-Correlation-Id header from upstream (API gateway, load balancer)
// or generates a new one. Propagates automatically to all log events.
app.UseSinkSharp();

// ── 4. Mount dashboard ────────────────────────────────────────────────────────
// https://localhost:5001/sinksharp/dashboard       ← live log stream
// https://localhost:5001/sinksharp/dashboard/stats ← JSON queue stats
app.UseSinkSharpDashboard("/sinksharp/dashboard");

// ── 5. Show what Azure context was detected ───────────────────────────────────
app.MapGet("/", (ILogger<Program> logger) =>
{
    // These are the actual values the enricher read from env vars at startup
    var azureInfo = new
    {
        siteName      = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")      ?? "(not set — running locally)",
        slot          = Environment.GetEnvironmentVariable("WEBSITE_SLOT_NAME")       ?? "(not set — running locally)",
        region        = Environment.GetEnvironmentVariable("REGION_NAME")             ?? "(not set — running locally)",
        resourceGroup = Environment.GetEnvironmentVariable("WEBSITE_RESOURCE_GROUP")  ?? "(not set — running locally)",
        podName       = Environment.GetEnvironmentVariable("POD_NAME")                ?? "(not set — not on AKS)",
        podNamespace  = Environment.GetEnvironmentVariable("POD_NAMESPACE")           ?? "(not set — not on AKS)",
    };

    logger.LogInformation("Root endpoint hit — Azure context: {@AzureInfo}", azureInfo);

    return Results.Ok(new
    {
        message   = "SinkSharp Azure Sample",
        dashboard = "https://localhost:5001/sinksharp/dashboard",
        endpoints = new[] { "/", "/orders", "/simulate", "/error" },
        azureContextDetected = azureInfo,
        tip = "Set env vars (WEBSITE_SITE_NAME, REGION_NAME etc.) to simulate Azure locally"
    });
});

// ── 6. Sample API endpoints ───────────────────────────────────────────────────
app.MapGet("/orders", (ILogger<Program> logger) =>
{
    logger.LogInformation("Listing orders from region {Region}",
        Environment.GetEnvironmentVariable("REGION_NAME") ?? "local");
    return Results.Ok(new[] {
        new { Id = 1, Status = "Shipped",    Amount = 149.99m },
        new { Id = 2, Status = "Processing", Amount = 89.50m  },
    });
});

app.MapGet("/simulate", async (ILogger<Program> logger) =>
{
    // Simulate a realistic request with multiple log levels + PII
    logger.LogInformation("Payment started for order {OrderId}", Random.Shared.Next(1000, 9999));
    await Task.Delay(30);
    logger.LogWarning("Payment gateway response slow: {Ms}ms", Random.Shared.Next(800, 2000));
    logger.LogInformation("Payment confirmed for {Email}", "customer@example.com");
    // → PII masked in dashboard: customer@***
    return Results.Ok(new { status = "Payment processed" });
});

app.MapGet("/error", (ILogger<Program> logger) =>
{
    try
    {
        throw new InvalidOperationException("Downstream service timeout");
    }
    catch (Exception ex)
    {
        // Error events use unbounded channel — never dropped even under load
        logger.LogError(ex, "PaymentGateway unavailable on slot {Slot}",
            Environment.GetEnvironmentVariable("WEBSITE_SLOT_NAME") ?? "local");
    }
    return Results.Problem("Service unavailable", statusCode: 503);
});

app.Run();
