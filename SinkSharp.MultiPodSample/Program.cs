// ═══════════════════════════════════════════════════════════════════════════════
// SinkSharp — Multi-Pod Dashboard Sample
// ═══════════════════════════════════════════════════════════════════════════════
// Demonstrates BOTH dashboard registration options:
//
//   Option 1 — AddSinkSharpDashboard()
//     Single pod, local dev, Docker single container.
//     No extra packages. In-memory SignalR transport.
//     Each pod shows only its own logs.
//
//   Option 2 — AddSinkSharpDashboardRedis()
//     Kubernetes 2+ replicas, Azure App Service with scaling.
//     Requires: dotnet add package Microsoft.AspNetCore.SignalR.StackExchangeRedis
//     Reuse your existing Redis — safe to share with cache/session/rate-limiting.
//     All pods stream to the same dashboard — browser sees unified log view.
//
// HOW TO SWITCH:
//   Search for "OPTION 1" and "OPTION 2" below.
//   Comment out Option 1 and uncomment Option 2 (and the csproj package ref).
//
// HOW REDIS BACKPLANE WORKS:
//   Pod A → SinkSharp → LogHub → Redis pub/sub ──┐
//                                                 ├──→ Browser (sees ALL pods)
//   Pod B → SinkSharp → LogHub → Redis pub/sub ──┘
//
//   Every pod publishes its log events to Redis. Any pod the browser connects
//   to receives events from ALL pods via Redis subscription.
//
// Run:  dotnet run
// Open: https://localhost:{port}/sinksharp/dashboard
// ═══════════════════════════════════════════════════════════════════════════════

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Core.Middleware;

var builder = WebApplication.CreateBuilder(args);

DashboardSink dashSink = null!;

builder.Services.AddSinkSharpWithHealth(options => options
    .MinimumLevel(LogLevel.Debug)
    .MinimumLevelFor("Microsoft", LogLevel.Warning)
    .WithCorrelationId()
    .WithEnvironment(builder.Environment.ApplicationName, builder.Environment.EnvironmentName)
    .WithPiiMasking()
    .WithDashboard(out dashSink)
    .WriteTo(new ColoredConsoleSink(showFullCategory: true)));

// ── OPTION 1: Single pod / local dev / Docker single container ────────────────
// No extra packages needed. In-memory SignalR — each pod shows its own logs.
builder.Services.AddSinkSharpDashboard(dashSink);

// ── OPTION 2: Multi-pod / Kubernetes / Azure App Service with scaling ─────────
// Step 1: Uncomment the package ref in the .csproj file
// Step 2: Comment out Option 1 above and uncomment below
// Step 3: Set Redis:ConnectionString in appsettings.json or environment variable
//
// builder.Services.AddSinkSharpDashboardRedis(dashSink, signalR =>
//     signalR.AddStackExchangeRedis(
//         builder.Configuration["Redis:ConnectionString"]!,
//         options => options.Configuration.ChannelPrefix =
//             StackExchange.Redis.RedisChannel.Literal("MyApp.SinkSharp")));
//
// Multiple apps on the same Redis — give each a unique channelPrefix:
// builder.Services.AddSinkSharpDashboardRedis(dashSink, signalR =>
//     signalR.AddStackExchangeRedis(redisConn,
//         o => o.Configuration.ChannelPrefix =
//             StackExchange.Redis.RedisChannel.Literal("LaborApi.SinkSharp")));

var app = builder.Build();

// Correlation ID middleware
app.UseSinkSharp();

// ── OPTION A: No auth (default — local dev, internal tools behind VPN)
app.UseSinkSharpDashboard("/sinksharp/dashboard");

// ── OPTION B: Require any authenticated user (uses your app's existing auth)
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.RequireAuthorization());

// ── OPTION C: Named policy (define your own rules in AddAuthorization())
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.RequireAuthorization("AdminOnly"));

// ── OPTION D: Role check
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.RequireRole("Admin", "DevOps"));

// ── OPTION E: IP allowlist — no user auth needed
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.AllowFrom("10.0.0.0/8", "192.168.0.0/16", "::1"));

// ── OPTION F: Basic auth — simplest, no AddAuthentication() needed
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.UseBasicAuth("admin", "secret123"));

// ── OPTION G: Escape hatch — full control over endpoint config
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.ConfigureEndpoints(ep => ep.RequireAuthorization("AdminOnly")));
app.MapHealthChecks("/health");

app.MapGet("/", () => new
{
    sample    = "Multi-Pod Dashboard Sample",
    dashboard = "/sinksharp/dashboard",
    health    = "/health",
    options   = new
    {
        option1 = "AddSinkSharpDashboard()        — single pod, no extra packages",
        option2 = "AddSinkSharpDashboardRedis()   — multi-pod, reuse existing Redis",
    },
    howRedisWorks = new
    {
        step1 = "Pod A publishes logs to Redis pub/sub channel",
        step2 = "Pod B subscribes and receives Pod A's events",
        step3 = "Browser connected to any pod sees ALL pods' logs",
        isolation = "Channel prefix 'MyApp.SinkSharp' — never conflicts with your cache/session keys",
    }
});

// Generates logs to demonstrate dashboard filtering and correlation tracing
app.MapGet("/simulate", (ILogger<Program> log) =>
{
    log.LogInformation("Request received on pod {PodName}",
        Environment.GetEnvironmentVariable("POD_NAME") ?? "local");
    log.LogDebug("Processing simulation step 1");
    log.LogWarning("Simulated warning — stock level low {Level}", 3);
    log.LogInformation("Order {OrderId} processed for {Email}",
        Guid.NewGuid().ToString("N")[..8],
        "customer@example.com");  // ← PII masked: customer@***.***
    return Results.Ok(new
    {
        message = "Logs generated — check dashboard",
        pod     = Environment.GetEnvironmentVariable("POD_NAME") ?? "local",
        tip     = "Run 2 pods and switch to Option 2 to see both pods in one dashboard"
    });
});

app.Run();
