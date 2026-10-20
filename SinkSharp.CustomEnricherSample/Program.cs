// ═══════════════════════════════════════════════════════════════════════════════
// SinkSharp — Custom Enricher Sample
// ═══════════════════════════════════════════════════════════════════════════════
// Demonstrates:
//   ✅ TenantEnricher     — multi-tenant SaaS: AsyncLocal tenant context
//   ✅ BuildInfoEnricher  — version/commit SHA cached at startup (zero runtime cost)
//   ✅ RequestEnricher    — RequestPath, Method, UserId via IHttpContextAccessor
//   ✅ How to register custom enrichers via .WithEnricher()
//   ✅ Nested scope override — inner scope overrides outer for that request
//
// Enricher implementations are in Enrichers.cs
// Run:  dotnet run
// Open: https://localhost:{port}/sinksharp/dashboard
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

builder.Services.AddHttpContextAccessor();

builder.Services.AddSinkSharp(options => options
    .MinimumLevel(LogLevel.Debug)
    .MinimumLevelFor("Microsoft", LogLevel.Warning)
    .WithCorrelationId()
    .WithEnvironment(builder.Environment.ApplicationName, builder.Environment.EnvironmentName)
    .WithPiiMasking()
    .WithEnricher(new TenantEnricher())       // adds TenantId from AsyncLocal
    .WithEnricher(new BuildInfoEnricher())    // adds AppVersion, BuildEnv, CommitSha
    .WithDashboard(out dashSink)
    .WriteTo(new ColoredConsoleSink(showFullCategory: true, showSourceContext: true))
    .WriteTo(new RollingFileSink("logs", "enricher-sample")));

builder.Services.AddSinkSharpDashboard(dashSink);
    // ── Multi-pod option — uncomment when running 2+ Kubernetes replicas ─────────
    // Requires: dotnet add package Microsoft.AspNetCore.SignalR.StackExchangeRedis
    // builder.Services.AddSinkSharpDashboardRedis(dashSink, signalR =>
    //     signalR.AddStackExchangeRedis(
    //         builder.Configuration["Redis:ConnectionString"]!,
    //         options => options.Configuration.ChannelPrefix =
    //             StackExchange.Redis.RedisChannel.Literal("MyApp.SinkSharp")));

var app = builder.Build();

app.UseSinkSharpDashboard("/sinksharp/dashboard");

// ── Correlation middleware ──────────────────────────────────────────────────
app.UseSinkSharp();

app.MapGet("/", () => new
{
    sample    = "Custom Enricher Sample",
    dashboard = "/sinksharp/dashboard",
    enrichers = new[] { "TenantEnricher", "BuildInfoEnricher" },
    tip       = "Pass X-Tenant-Id header to see TenantId in every log event",
});

// Every log from this endpoint has: TenantId, AppVersion, CommitSha, CorrelationId
app.MapGet("/order/{id:int}", (int id, ILogger<Program> log) =>
{
    // TenantId comes from X-Tenant-Id header set in middleware (or "tenant-default")
    // Check logs — every event has TenantId, AppVersion, CommitSha, CorrelationId
    log.LogInformation("Fetching order {OrderId} for current tenant", id);
    return Results.Ok(new
    {
        Id     = id,
        Status = "Shipped",
        tip    = "Set X-Tenant-Id header to change TenantId in logs"
    });
});

// No header needed — tenantId comes from the URL path and overrides middleware tenant
app.MapGet("/order/new", (ILogger<Program> log) =>
{
    log.LogInformation("New order created for default tenant");
    return Results.Ok(new { Id = Random.Shared.Next(100, 999), Status = "Pending" });
});

// Nested scope — override tenant mid-request
app.MapGet("/tenant/{tenantId}/order", (string tenantId, ILogger<Program> log) =>
{
    // Override TenantId via BeginScope — captured on caller thread ✅
    using var _ = log.BeginScope(new Dictionary<string, object> { ["TenantId"] = tenantId });
    log.LogInformation("Order created for explicit tenant {TenantId} override", tenantId);
    return Results.Ok(new { Id = 99, TenantId = tenantId, Status = "Created" });
});

app.Run();
