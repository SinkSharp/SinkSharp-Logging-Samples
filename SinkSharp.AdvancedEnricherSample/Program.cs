// ═══════════════════════════════════════════════════════════════════════════════
// SinkSharp — Advanced Enricher Sample
// ═══════════════════════════════════════════════════════════════════════════════
// Demonstrates all v1.1 enricher and pipeline features:
//
//   ✅ WithMachineName()            — pod/machine name without full environment block
//   ✅ AddSinkSharpRequestId()      — stamps RequestId from ASP.NET TraceIdentifier
//   ✅ AddSinkSharpUserContext()    — stamps UserId, UserName, UserRole (auth only)
//   ✅ AddSinkSharpRequestPath()    — stamps HttpMethod + RequestPath
//   ✅ AddSinkSharpElapsedTime()    — stamps ElapsedMs since request start
//   ✅ .Filter()                    — drop events before enqueue, zero allocation
//   ✅ .WithEnricher(when:)         — conditional enrichment by level or category
//   ✅ SetGlobalLevel()             — change minimum level at runtime, no restart
//   ✅ app.UseSinkSharp()           — X-Correlation-Id + ElapsedTime stopwatch
//
// Run:  dotnet run
// Open: https://localhost:5001/sinksharp/dashboard
// ═══════════════════════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Middleware;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.File;

var builder = WebApplication.CreateBuilder(args);
var appName = builder.Environment.ApplicationName;
var env     = builder.Environment.EnvironmentName;

DashboardSink dashSink = null!;

// ── Step 1: Register IHttpContextAccessor ─────────────────────────────────────
builder.Services.AddHttpContextAccessor();

// ── Step 2: Register SinkSharp ────────────────────────────────────────────────
builder.Services.AddSinkSharpWithHealth(options => options
    .MinimumLevel(LogLevel.Debug)
    .MinimumLevelFor("Microsoft", LogLevel.Warning)
    .MinimumLevelFor("System.Net", LogLevel.Error)

    // Core enrichers — work in ALL project types
    .WithCorrelationId()
    .WithEnvironment(appName, env)
    .WithMachineName()
    .WithPiiMasking()

    // Structured filters — drop before enqueue, zero allocation
    .Filter(e => !e.MessageTemplate.Contains("health", StringComparison.OrdinalIgnoreCase))
    .Filter(e => !e.MessageTemplate.Contains("ping",   StringComparison.OrdinalIgnoreCase))

    // Conditional enrichment — UtcTimestamp only on Warning+
    .WithEnricher(new TimestampEnricher(), when: e => e.Level >= LogLevel.Warning)

    // Sinks with per-sink minimum levels:
    //   Console  : Debug+        — see everything during development
    //   File     : Information+  — all non-debug events written to logs/
    //   Dashboard: Information+  — no Debug noise in the real-time UI
    .WithDashboard(out dashSink,                             LogLevel.Information)
    .WriteTo(new ColoredConsoleSink(showFullCategory: true),LogLevel.Debug)
    .WriteTo(new RollingFileSink("logs", appName),           LogLevel.Information));

builder.Services.AddSinkSharpDashboard(dashSink);

// ── Step 3: Register HTTP context enricher DI helpers ─────────────────────────
// One line each — wired automatically after the DI container is built.
// All are optional — register only what you need.
// All silently skip when HttpContext is null (Worker Service, Console App, background jobs).
builder.Services.AddSinkSharpRequestId();    // stamps RequestId  from TraceIdentifier
builder.Services.AddSinkSharpRequestPath();  // stamps HttpMethod + RequestPath
builder.Services.AddSinkSharpElapsedTime();  // stamps ElapsedMs  since request start
builder.Services.AddSinkSharpUserContext();  // stamps UserId, UserName, UserRole (auth only)

// ── Step 4: Build the app ─────────────────────────────────────────────────────
var app = builder.Build();

// ── Step 5: UseSinkSharp() middleware ─────────────────────────────────────────
// • Reads/generates X-Correlation-Id
// • Sets CorrelationContext.Current via AsyncLocal
// • Starts Stopwatch in HttpContext.Items → read by ElapsedTimeEnricher
// • Writes X-Correlation-Id to response header
app.UseSinkSharp();

app.UseSinkSharpDashboard("/sinksharp/dashboard");
app.MapHealthChecks("/health");

// ── Endpoints ─────────────────────────────────────────────────────────────────

app.MapGet("/", () => new
{
    sample   = "Advanced Enricher Sample",
    features = new[]
    {
        "WithMachineName()         → MachineName",
        "RequestIdEnricher         → RequestId from ASP.NET TraceIdentifier",
        "RequestPathEnricher       → HttpMethod + RequestPath",
        "ElapsedTimeEnricher       → ElapsedMs since request start",
        "UserContextEnricher       → UserId, UserName, UserRole (authenticated only)",
        ".Filter()                 → health/ping messages never reach sinks",
        ".WithEnricher(when: Warn+)→ UtcTimestamp only on Warning+",
        "SetGlobalLevel()          → try PUT /debug/level/Debug",
    },
    sinks = new
    {
        console   = "Debug+  — all events",
        dashboard = "Information+ — no Debug noise",
        file      = "Information+ — check logs/ folder for .jsonl files",
    },
    dashboard = "/sinksharp/dashboard",
    orders    = "/orders",
    ordersWithElapsed = "/orders/1  (see ElapsedMs in console + dashboard Properties)",
    ordersWarning     = "/orders/1000  (triggers Warning → UtcTimestamp + file log)",
    debug     = "/debug/level",
});

app.MapGet("/orders", (ILogger<Program> log) =>
{
    // ElapsedMs will be stamped on these events — shows time since request entry
    log.LogDebug("Querying orders database");
    log.LogInformation("Listing all orders");
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
        log.LogError(ex, "Order payment failed for user {Email}", "buyer@example.com");
        // PII masked → buyer@***
    }
    return Results.Problem("Payment failed");
});

// Filtered — never reaches any sink
app.MapGet("/health-check", (ILogger<Program> log) =>
{
    log.LogInformation("health check ping received");
    return Results.Ok(new { status = "ok" });
});

// ── Dynamic level endpoints ────────────────────────────────────────────────────
app.MapGet("/debug/level", () => new
{
    currentRuntimeLevel = SinkSharpLoggerFactory.GetGlobalLevel().ToString(),
    startupLevel        = "Debug",
    note                = "PUT /debug/level/{level} — Trace/Debug/Information/Warning/Error/Critical/None",
});

app.MapPut("/debug/level/{level}", (string level, ILogger<Program> log) =>
{
    if (!Enum.TryParse<LogLevel>(level, ignoreCase: true, out var logLevel))
        return Results.BadRequest(new { error = $"Unknown level: {level}" });

    var previous = SinkSharpLoggerFactory.GetGlobalLevel();
    SinkSharpLoggerFactory.SetGlobalLevel(logLevel);

    log.LogWarning("Global log level changed from {Previous} to {New}", previous, logLevel);

    return Results.Ok(new
    {
        previous = previous.ToString(),
        current  = logLevel.ToString(),
        note     = logLevel == LogLevel.None
            ? "Restored to startup level (Debug)"
            : $"Now using {logLevel}+ globally",
    });
});

app.Run();

// ── Custom conditional enricher ───────────────────────────────────────────────
/// <summary>
/// Stamps UtcTimestamp on Warning+ events only.
/// Registered via .WithEnricher(new TimestampEnricher(), when: e => e.Level >= LogLevel.Warning)
/// </summary>
public sealed class TimestampEnricher : SinkSharp.Core.Abstractions.IEnricher
{
    public void Enrich(SinkSharp.Core.LogEvent logEvent, SinkSharp.Core.Abstractions.IPropertyBag bag)
        => bag.AddOrUpdate("UtcTimestamp", DateTime.UtcNow.ToString("O"));
}
