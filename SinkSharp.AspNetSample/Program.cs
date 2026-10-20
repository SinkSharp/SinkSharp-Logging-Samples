using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.File;
using SinkSharp.Core.Middleware;

var builder = WebApplication.CreateBuilder(args);
var appName = builder.Environment.ApplicationName;
var env     = builder.Environment.EnvironmentName;

// ── Register SinkSharp with health check ─────────────────────────────────────
builder.Services.AddSinkSharpWithHealth(options => options
    .MinimumLevel(LogLevel.Information)
    .MinimumLevelFor("Microsoft",     LogLevel.Warning)  // silence ASP.NET noise
    .MinimumLevelFor("System.Net",    LogLevel.Error)
    .WithCorrelationId()
    .WithEnvironment(appName, env)
    .WithPiiMasking()
    // Per-sink minimum levels — console gets everything, file keeps only Warning+
    .WriteTo(new ColoredConsoleSink())
    .WriteTo(new RollingFileSink("logs", appName), LogLevel.Warning));

builder.Services.AddHealthChecks();

var app = builder.Build();

// ── Correlation middleware ──────────────────────────────────────────────────
app.UseSinkSharp();

app.MapGet("/", (ILogger<Program> logger) =>
{
    logger.LogInformation("Hello endpoint called");
    return "SinkSharp is running!";
});

app.MapGet("/error", (ILogger<Program> logger) =>
{
    logger.LogError("Simulated error for demonstration");
    return Results.Problem("Something went wrong");
});

// GET /health → Healthy / Degraded / Unhealthy
app.MapHealthChecks("/health");

app.Run();
