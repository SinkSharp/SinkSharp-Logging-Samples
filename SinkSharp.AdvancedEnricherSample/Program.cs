// ═══════════════════════════════════════════════════════════════════════════════
// SinkSharp — Advanced Enricher Sample
// ═══════════════════════════════════════════════════════════════════════════════
// Demonstrates all v1.1 enricher and pipeline features:
//
//   ✅ WithMachineName()          — pod/machine name without full environment block
//   ✅ WithRequestId(accessor)    — ASP.NET TraceIdentifier on every event
//   ✅ WithUserContext(accessor)  — UserId, UserName, UserRole (authenticated only)
//   ✅ WithRequestPath(accessor)  — HttpMethod + RequestPath on every event
//   ✅ WithElapsedTime(accessor)  — ElapsedMs since request start on every event
//   ✅ .Filter()                  — drop specific events before enqueue (zero alloc)
//   ✅ .WithEnricher(when:)       — conditional enrichment by level or category
//   ✅ SetGlobalLevel()           — change minimum level at runtime without restart
//   ✅ app.UseSinkSharp()         — X-Correlation-Id + ElapsedTime stopwatch in one call
//   ✅ AddSinkSharpRequestId()    — DI helper resolving IHttpContextAccessor correctly
//   ✅ AddSinkSharpUserContext()  — stamps authenticated user on every event
//   ✅ AddSinkSharpRequestPath()  — stamps HttpMethod + RequestPath on every event
//   ✅ AddSinkSharpElapsedTime()  — stamps ElapsedMs on every event
//
// Run:  dotnet run
// Open: https://localhost:5001/sinksharp/dashboard
//
// Endpoints to try:
//   GET /               — home
//   GET /orders         — Info logs (visible in console + dashboard)
//   GET /orders/42      — Info + structured properties
//   GET /orders/fail    — Error log with exception
//   GET /health-check   — dropped by filter (never reaches any sink)
//   GET /debug/level    — show current global level
//   PUT /debug/level/Debug    — enable debug logs at runtime
//   PUT /debug/level/Information — restore normal level
// ═══════════════════════════════════════════════════════════════════════════════

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Core.Middleware;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.File;

var builder = WebApplication.CreateBuilder(args);
var appName = builder.Environment.ApplicationName;
var env     = builder.Environment.EnvironmentName;

DashboardSink dashSink = null!;

// ── Step 1: Register IHttpContextAccessor ─────────────────────────────────────
// Required before any HTTP context enrichers (WithRequestId, WithUserContext,
// WithRequestPath, WithElapsedTime). One call enables all four.
builder.Services.AddHttpContextAccessor();

// ── Step 2: Register SinkSharp ────────────────────────────────────────────────
builder.Services.AddSinkSharpWithHealth(options => options
    .MinimumLevel(LogLevel.Debug)                     // allow Debug — runtime level controls it
    .MinimumLevelFor("Microsoft", LogLevel.Warning)
    .MinimumLevelFor("System.Net", LogLevel.Error)

    // ── Core enrichers — work in ALL project types ────────────────────────────
    .WithCorrelationId()                              // X-Correlation-Id from AsyncLocal
    .WithEnvironment(appName, env)                    // AppName, Environment, MachineName
    .WithMachineName()                                // standalone if you skip WithEnvironment
    .WithPiiMasking()

    // ── Structured filter — drop events BEFORE enqueue, zero allocation ───────
    // Health check pings generate constant noise — drop them entirely.
    // Events that don't pass are returned to the pool immediately.
    .Filter(e => !e.MessageTemplate.Contains("health", StringComparison.OrdinalIgnoreCase))
    .Filter(e => !e.MessageTemplate.Contains("ping",   StringComparison.OrdinalIgnoreCase))

    // ── Conditional enrichment — run expensive enrichers selectively ──────────
    // This custom enricher stamps the current UTC timestamp in ISO format.
    // Only runs for Warning+ — no point adding it to every Debug/Info event.
    .WithEnricher(new TimestampEnricher(), when: e => e.Level >= LogLevel.Warning)

    // ── Dashboard with per-sink minimum level ─────────────────────────────────
    .WithDashboard(out dashSink, minLevel: LogLevel.Information)

    // ── Sinks with per-sink levels ────────────────────────────────────────────
    .WriteTo(new ColoredConsoleSink(showFullCategory: true), LogLevel.Debug)
    .WriteTo(new RollingFileSink("logs", appName),           LogLevel.Warning));

builder.Services.AddSinkSharpDashboard(dashSink);

// ── Step 3: Register HTTP context enricher DI helpers ─────────────────────────
// Each resolves IHttpContextAccessor from the built container — correct timing.
// All are optional: register only the ones you need.
// All silently skip when HttpContext is null (background jobs, non-request code).
builder.Services.AddSinkSharpRequestId();    // stamps RequestId  = "0HN8L2I74S55P:00000001"
builder.Services.AddSinkSharpUserContext();  // stamps UserId, UserName, UserRole (auth only)
builder.Services.AddSinkSharpRequestPath(); // stamps HttpMethod = "GET", RequestPath = "/orders"
builder.Services.AddSinkSharpElapsedTime(); // stamps ElapsedMs  = 43  (since request start)

var app = builder.Build();

// ── Step 4: UseSinkSharp() in the middleware pipeline ─────────────────────────
// One call does:
//   • Reads X-Correlation-Id from request header (or generates a 12-char GUID)
//   • Sets CorrelationContext.Current via AsyncLocal — flows through all awaits
//   • Starts a Stopwatch in HttpContext.Items — WithElapsedTime() reads it
//   • Writes X-Correlation-Id to the response header
// Place before UseAuthentication/UseAuthorization so correlation is set for
// all middleware including auth.
app.UseSinkSharp();

app.UseSinkSharpDashboard("/sinksharp/dashboard");
app.MapHealthChecks("/health");

// ── Endpoints ─────────────────────────────────────────────────────────────────

app.MapGet("/", () => new
{
    sample   = "Advanced Enricher Sample",
    features = new[]
    {
        "WithMachineName()",
        "WithRequestId()   → RequestId from ASP.NET TraceIdentifier",
        "WithUserContext() → UserId, UserName, UserRole (authenticated only)",
        "WithRequestPath() → HttpMethod + RequestPath",
        "WithElapsedTime() → ElapsedMs since request start",
        ".Filter()         → health/ping messages never reach sinks",
        ".WithEnricher(when: Warning+) → expensive enricher runs selectively",
        "SetGlobalLevel()  → change level at runtime (try PUT /debug/level/Debug)",
    },
    dashboard = "/sinksharp/dashboard",
    health    = "/health",
    orders    = "/orders",
    debug     = "/debug/level",
});

app.MapGet("/orders", (ILogger<Program> log) =>
{
    // Each event carries: CorrelationId, RequestId, HttpMethod, RequestPath, ElapsedMs
    log.LogDebug("Querying orders database");         // console only (Debug+)
    log.LogInformation("Listing all orders");          // console + dashboard (Info+)
    return Results.Ok(new[]
    {
        new { Id = 1, Status = "Shipped",    Amount = 149.99m },
        new { Id = 2, Status = "Processing", Amount = 89.50m  },
        new { Id = 3, Status = "Pending",    Amount = 299.00m },
    });
});

app.MapGet("/orders/{id:int}", (int id, ILogger<Program> log) =>
{
    log.LogDebug("Fetching order {OrderId} from database", id);
    log.LogInformation("Order {OrderId} retrieved", id);

    if (id > 100)
    {
        // Warning+ events also get TimestampEnricher.UtcTimestamp stamped
        log.LogWarning("Order {OrderId} not found in primary store, checking archive", id);
        return Results.NotFound(new { error = $"Order {id} not found" });
    }

    return Results.Ok(new { Id = id, Status = "Processing", Amount = 99.99m });
});

app.MapGet("/orders/fail", (ILogger<Program> log) =>
{
    try { throw new InvalidOperationException("Payment gateway timeout after 30s"); }
    catch (Exception ex)
    {
        // Error events: TimestampEnricher runs, all sinks receive it
        log.LogError(ex, "Order payment failed for user {Email}", "buyer@example.com");
        // PII masked → "buyer@***"
    }
    return Results.Problem("Payment failed");
});

// This endpoint's log message contains "health" — the filter drops it before enqueue.
// You will NOT see any log from this endpoint in console, file, or dashboard.
app.MapGet("/health-check", (ILogger<Program> log) =>
{
    log.LogInformation("health check ping received");  // ← filtered out, never reaches sinks
    return Results.Ok(new { status = "ok" });
});

// ── Dynamic level endpoints ────────────────────────────────────────────────────
// #11 — SetGlobalLevel() lets you change the minimum level at runtime without restart.
// Useful for diagnosing production issues temporarily.

app.MapGet("/debug/level", () => new
{
    currentRuntimeLevel = SinkSharpLoggerFactory.GetGlobalLevel().ToString(),
    startupLevel        = "Debug (set at startup)",
    note = "PUT /debug/level/{level} to change. Valid: Trace Debug Information Warning Error Critical None",
});

app.MapPut("/debug/level/{level}", (string level, ILogger<Program> log) =>
{
    if (!Enum.TryParse<LogLevel>(level, ignoreCase: true, out var logLevel))
        return Results.BadRequest(new { error = $"Unknown level: {level}. Use Trace/Debug/Information/Warning/Error/Critical/None" });

    var previous = SinkSharpLoggerFactory.GetGlobalLevel();
    SinkSharpLoggerFactory.SetGlobalLevel(logLevel);

    log.LogWarning(
        "Global log level changed from {Previous} to {New} by {RequestPath}",
        previous, logLevel, "/debug/level/" + level);

    return Results.Ok(new
    {
        previous = previous.ToString(),
        current  = logLevel.ToString(),
        note     = logLevel == LogLevel.None
            ? "Restored to startup level (Debug)"
            : $"All loggers now use {logLevel}+ globally",
    });
});

app.Run();

// ── Custom enricher example for .WithEnricher(when:) ─────────────────────────
/// <summary>
/// Example of a custom conditional enricher. Registered with:
///   .WithEnricher(new TimestampEnricher(), when: e => e.Level >= LogLevel.Warning)
/// Only runs for Warning/Error/Critical — skipped entirely for Debug/Info.
/// </summary>
public sealed class TimestampEnricher : SinkSharp.Core.Abstractions.IEnricher
{
    public void Enrich(SinkSharp.Core.LogEvent logEvent, SinkSharp.Core.Abstractions.IPropertyBag bag)
        => bag.AddOrUpdate("UtcTimestamp", DateTime.UtcNow.ToString("O"));
}
