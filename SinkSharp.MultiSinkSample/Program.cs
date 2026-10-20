// ═══════════════════════════════════════════════════════════════════════════════
// SinkSharp — Multi-Sink Sample
// ═══════════════════════════════════════════════════════════════════════════════
// Demonstrates:
//   ✅ Routing events to multiple sinks simultaneously
//   ✅ Console + File + Dashboard all active at once
//   ✅ Per-sink minimum level — console gets Debug+, file gets Warning+ only
//   ✅ Dashboard minimum level — Information+ (skip Debug noise in the UI)
//   ✅ Fan-out pipeline: eligible sinks run concurrently on the drainer thread
//   ✅ Per-category routing: audit category goes to a separate log file
//   ✅ Audit sink pattern: critical business events to a separate file
//   ✅ Queue stats: see all events processed across all sinks
//
// Per-sink level quick reference:
//   .WriteTo(sink)                     → no guard, sink receives everything the global level passes
//   .WriteTo(sink, LogLevel.Warning)   → sink only receives Warning / Error / Critical
//   .WithDashboard(out d, LogLevel.Information) → dashboard skips Debug noise
//
// Run:  dotnet run
// Open: https://localhost:5001/sinksharp/dashboard
//
// Log files created:
//   logs/multisink-YYYY-MM-DD.jsonl    ← Warning+ only (reduced file noise)
//   logs/audit-YYYY-MM-DD.jsonl        ← audit events only
// ═══════════════════════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.File;
using SinkSharp.Core.Middleware;

// ── Top-level program statements ──────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

DashboardSink dashSink = null!;

builder.Services.AddSinkSharpWithHealth(options => options
    .MinimumLevel(LogLevel.Debug)                             // global gate — all sinks see Debug+
    .MinimumLevelFor("Microsoft", LogLevel.Warning)
    .MinimumLevelFor("System.Net", LogLevel.Error)
    .WithCorrelationId()
    .WithEnvironment(builder.Environment.ApplicationName, builder.Environment.EnvironmentName)
    .WithPiiMasking()

    // Per-sink minimum levels — each sink gets exactly the verbosity it needs:
    //
    //   Console  : Debug+        — full detail for local dev and tailing in CI
    //   File     : Warning+      — production files stay small; errors and warnings only
    //   Dashboard: Information+  — skip Debug noise in the real-time UI
    //   AuditFile: no guard      — AuditFileSink filters by category internally
    //
    .WithDashboard(out dashSink, minLevel: LogLevel.Information)
    .WriteTo(new ColoredConsoleSink(showFullCategory: true),  LogLevel.Debug)
    .WriteTo(new RollingFileSink("logs", "multisink"),        LogLevel.Warning)
    .WriteTo(new AuditFileSink()));

builder.Services.AddSinkSharpDashboard(dashSink);
    // ── Multi-pod option — uncomment when running 2+ Kubernetes replicas ─────────
    // Requires: dotnet add package Microsoft.AspNetCore.SignalR.StackExchangeRedis
    // builder.Services.AddSinkSharpDashboardRedis(dashSink, signalR =>
    //     signalR.AddStackExchangeRedis(
    //         builder.Configuration["Redis:ConnectionString"]!,
    //         options => options.Configuration.ChannelPrefix =
    //             StackExchange.Redis.RedisChannel.Literal("MyApp.SinkSharp")));

var app = builder.Build();

app.UseSinkSharp();

app.UseSinkSharpDashboard("/sinksharp/dashboard");
app.MapHealthChecks("/health");

app.MapGet("/", () => new
{
    sample    = "Multi-Sink Sample",
    sinks     = new[]
    {
        "ColoredConsoleSink (Debug+)",
        "RollingFileSink (Warning+ — file stays small)",
        "DashboardSink (Information+ — no Debug noise in UI)",
        "AuditFileSink (all levels, category-filtered internally)",
    },
    files  = new[] { "logs/multisink-YYYY-MM-DD.jsonl", "logs/audit-YYYY-MM-DD.jsonl" },
});

// Regular endpoint — Info/Warning/Error go to console + dashboard; Warning+ also to file
app.MapGet("/order/{id:int}", (int id, ILogger<Program> log) =>
{
    log.LogDebug("Fetching order {OrderId} from database", id);         // console only
    log.LogInformation("Order {OrderId} viewed", id);                   // console + dashboard
    return Results.Ok(new { Id = id, Status = "Shipped" });
});

// Audit endpoint — "Audit" category routes to AuditFileSink AND all other sinks
app.MapPost("/order/{id:int}/approve", (int id, ILoggerFactory loggerFactory) =>
{
    var audit = loggerFactory.CreateLogger("Audit.Orders");
    audit.LogInformation("Order {OrderId} approved — compliance event recorded", id);
    return Results.Ok(new { Id = id, Status = "Approved", Audited = true });
});

// Generates all log levels — good for seeing per-sink filtering in action
// Debug   → console only         (file and dashboard skip it)
// Info    → console + dashboard  (file skips it)
// Warning → console + file + dashboard
// Error   → all sinks
app.MapGet("/demo", async (ILogger<Program> log) =>
{
    log.LogDebug   ("Debug — console only (file + dashboard skip Debug)");
    log.LogInformation("Info — console + dashboard (file skips Info)");
    log.LogWarning ("Warning — all three sinks: console, file, dashboard");
    try { throw new TimeoutException("External service timeout"); }
    catch (Exception ex)
    {
        log.LogError(ex, "Error — all sinks receive Error+, {Service}", "InventoryService");
    }
    await Task.Delay(100);
    return Results.Ok(new
    {
        message = "Demo complete — compare console output vs file vs dashboard",
        tip     = "File has Warning+ only. Dashboard has Information+. Console has everything.",
    });
});

app.Run();
