// ════════════════════════════════════════════════════════════════════════════════
// SinkSharp.OpenTelemetrySample — OpenTelemetry Integration
// ════════════════════════════════════════════════════════════════════════════════
//
// This sample demonstrates how SinkSharp integrates with any OpenTelemetry backend
// using a single endpoint — Jaeger, Grafana + Loki, or Datadog — with zero
// per-backend code changes. Switch backends by changing OTEL_BACKEND env var only.
//
// ── HOW IT WORKS ─────────────────────────────────────────────────────────────
//
//   SinkSharp pipeline (drainer thread):
//     WithCorrelationId()  → stamps CorrelationId on every event
//     WithPiiMasking()     → masks email / phone / card / SSN BEFORE export
//     WithUserContext()    → stamps UserId, UserName, UserRole from JWT
//     OtlpLogSink          → sends fully enriched, PII-free log records via OTLP
//                            directly from drainer thread — no MEL bridge needed
//
//   OpenTelemetry tracing (separate):
//     AddSinkSharpTracing() → HTTP request spans → same OTLP endpoint
//                             span events (Activity.AddEvent) visible in Jaeger Logs tab
//
//   Key benefit: PII masking runs IN-PROCESS before any data leaves the app.
//   Serilog, NLog, and the standard MEL bridge all require Collector-side redaction —
//   meaning PII reaches the network. SinkSharp masks it on the drainer thread first.
//
// ── THREE BACKENDS — same code, change OTEL_BACKEND only ────────────────────
//
//   OPTION 1 — Jaeger (local dev, traces + logs):
//     docker compose --profile jaeger up -d
//     dotnet run --launch-profile Jaeger
//     → http://localhost:16686
//
//   OPTION 2 — Grafana + Loki + Jaeger (logs in Loki, traces in Jaeger):
//     docker compose --profile grafana up -d
//     dotnet run --launch-profile Grafana
//     Logs:   http://localhost:3000 → Explore → Loki
//     Traces: http://localhost:16686
//
//   OPTION 3 — Datadog (cloud-native, no Docker needed):
//     dotnet run --launch-profile Datadog   (set DD_API_KEY first)
//     → https://app.datadoghq.com/logs
//
// ════════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using SinkSharp.Core;
using SinkSharp.Core.Middleware;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.OpenTelemetry;   // OtlpLogSink, OtlpLogSinkOptions, AddSinkSharpTracing

var builder = WebApplication.CreateBuilder(args);
var appName = builder.Environment.ApplicationName;
var env     = builder.Environment.EnvironmentName;

// ── 1. Select backend ─────────────────────────────────────────────────────────
var backend  = builder.Configuration["OTEL_BACKEND"] ?? "jaeger";
var ddApiKey = builder.Configuration["DD_API_KEY"]   ?? "";

// ── 2. OtlpLogSinkOptions — single config object for logs AND traces ──────────
// Same endpoint, same headers used for both OtlpLogSink and AddSinkSharpTracing.
// Switch backends by changing these values only — no other code changes needed.
var otlpOptions = backend.ToLowerInvariant() switch
{
    // Jaeger: OtlpLogSink sends log records to Jaeger on port 4318 (HTTP/Protobuf).
    // Note: Jaeger UI does not have a standalone log viewer — logs are visible
    // inside each trace span's "Logs" tab via Activity.AddEvent() span events.
    // To see logs in a UI, use the Grafana profile instead.
    "jaeger" => new OtlpLogSinkOptions
    {
        Endpoint              = new Uri("http://localhost:4317"),
        Protocol              = OtlpProtocol.Grpc,
        ServiceName           = appName,
        ServiceVersion        = "1.0.0",
        DeploymentEnvironment = env,
        BatchSize             = 512,
        FlushInterval         = TimeSpan.FromSeconds(5),
    },

    // Grafana: logs and traces both route through the OTel Collector on port 4317.
    // Collector routes: logs → Loki, traces → Jaeger (see otel-collector.yaml).
    "grafana" => new OtlpLogSinkOptions
    {
        Endpoint              = new Uri("http://localhost:4317"),
        Protocol              = OtlpProtocol.Grpc,
        ServiceName           = appName,
        ServiceVersion        = "1.0.0",
        DeploymentEnvironment = env,
        BatchSize             = 512,
        FlushInterval         = TimeSpan.FromSeconds(5),
    },

    // Datadog: HTTP intake with DD-API-KEY header — no Agent needed
    "datadog" => new OtlpLogSinkOptions
    {
        Endpoint              = new Uri("https://http-intake.logs.datadoghq.com/api/v2/logs"),
        Protocol              = OtlpProtocol.HttpProtobuf,
        Headers               = new() { ["DD-API-KEY"] = ddApiKey },
        ServiceName           = appName,
        ServiceVersion        = "1.0.0",
        DeploymentEnvironment = env,
        BatchSize             = 512,
        FlushInterval         = TimeSpan.FromSeconds(5),
        ExportTimeout         = TimeSpan.FromSeconds(30), // higher for HTTPS cloud endpoint
        RetryCount            = 3,
    },

    _ => throw new InvalidOperationException(
        $"Unknown OTEL_BACKEND '{backend}'. Valid values: jaeger | grafana | datadog")
};

// ── 3. SinkSharp pipeline ─────────────────────────────────────────────────────
// Enrichers run first on the drainer thread. OtlpLogSink receives events after
// PII masking and enrichment — fully clean, fully structured.
DashboardSink dashSink = null!;
builder.Services.AddHttpContextAccessor();

builder.Services.AddSinkSharpWithHealth(options => options
    .MinimumLevel(LogLevel.Information)
    .MinimumLevelFor("Microsoft", LogLevel.Warning)
    .MinimumLevelFor("System.Net", LogLevel.Error)
    .WithCorrelationId()         // → correlation.id attribute on every OTel log record
    .WithEnvironment(appName, env) // → AppName, Environment, MachineName
    .WithPiiMasking()            // → email/phone/card/SSN masked BEFORE OtlpLogSink sees event
    .WithUserContext()           // → UserId, UserName, UserRole from JWT claims
    .WithDashboard(out dashSink) // → SinkSharp real-time web dashboard
    .WriteTo(new ColoredConsoleSink(), LogLevel.Debug)
    // OtlpLogSink: the SinkSharp.Sinks.OpenTelemetry ISink.
    // Receives events from the drainer thread — fully enriched and PII-masked.
    // Sends OTLP log records directly to the backend, no MEL bridge required.
    .WriteTo(new OtlpLogSink(otlpOptions), LogLevel.Information));

builder.Services.AddSinkSharpDashboard(dashSink);
builder.Services.AddSinkSharpRequestId();
builder.Services.AddSinkSharpRequestPath();
builder.Services.AddSinkSharpElapsedTime();

// ── 4. AddSinkSharpTracing — HTTP request spans to same OTLP endpoint ─────────
// Traces go to the same endpoint as logs (otlpOptions.Endpoint).
// In Grafana mode: OTel Collector routes traces → Jaeger, logs → Loki.
// In Jaeger mode:  Jaeger receives both directly.
// In Datadog mode: Datadog receives both via HTTP/Protobuf.
// Trace exporter always uses gRPC 4317 — Jaeger and OTel Collector both
// accept traces on gRPC 4317. OtlpLogSink uses HTTP/Protobuf 4318 separately.
// For Datadog: override both to HTTP intake endpoint.
var traceEndpoint = backend == "datadog"
    ? otlpOptions.Endpoint
    : new Uri("http://localhost:4317");
var traceProtocol = backend == "datadog"
    ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
    : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;

builder.Services.AddSinkSharpTracing(otlpOptions, tracing => tracing
    .AddAspNetCoreInstrumentation(o =>
    {
        o.RecordException       = true;   // exception on /error → error span in Jaeger
        o.EnrichWithHttpRequest = (activity, req) =>
            activity.SetTag("correlation.id",
                req.Headers["X-Correlation-Id"].FirstOrDefault() ?? "");
    })
    .AddHttpClientInstrumentation()      // outbound app HTTP calls as child spans
    .AddOtlpExporter(o =>
    {
        o.Endpoint            = traceEndpoint;
        o.Protocol            = traceProtocol;
        o.TimeoutMilliseconds = (int)otlpOptions.ExportTimeout.TotalMilliseconds;
        if (otlpOptions.Headers is { Count: > 0 })
            o.Headers = string.Join(",",
                otlpOptions.Headers.Select(kv => $"{kv.Key}={kv.Value}"));
    }));

// ── 5. Middleware ─────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseSinkSharp();
app.UseSinkSharpDashboard("/sinksharp/dashboard");
app.MapHealthChecks("/health");

// ── 6. Endpoints ──────────────────────────────────────────────────────────────

app.MapGet("/", () => new
{
    app       = "SinkSharp OpenTelemetry Sample",
    backend   = backend,
    dashboard = "/sinksharp/dashboard",
    endpoints = new[] { "/orders", "/orders/{id}", "/simulate", "/error", "/slow" },
    tip       = backend switch
    {
        "jaeger"  => "Traces + Logs: http://localhost:16686 (service: SinkSharp.OpenTelemetrySample)",
        "grafana" => "Logs: http://localhost:3000 → Explore → Loki | Traces: http://localhost:16686",
        "datadog" => "https://app.datadoghq.com/logs → service:SinkSharp.OpenTelemetrySample",
        _         => "Check your OTel backend"
    }
});

// Structured log + span event in Jaeger
app.MapGet("/orders", (ILogger<Program> logger) =>
{
    logger.LogInformation("Listing all orders");
    Activity.Current?.AddEvent(new ActivityEvent("listing-orders"));
    return Results.Ok(new[]
    {
        new { Id = 1, Status = "Shipped",    Amount = 149.99m },
        new { Id = 2, Status = "Processing", Amount = 89.50m  },
        new { Id = 3, Status = "Pending",    Amount = 299.00m },
    });
});

// Structured property as OTel attribute
app.MapGet("/orders/{id:int}", (int id, ILogger<Program> logger) =>
{
    logger.LogInformation("Fetching order {OrderId}", id);
    if (id > 100)
    {
        logger.LogWarning("Order {OrderId} not found — returning 404", id);
        return Results.NotFound(new { error = $"Order {id} not found" });
    }
    return Results.Ok(new { Id = id, Status = "Processing", Amount = 99.99m });
});

// PII masking demo — email masked BEFORE OtlpLogSink, before Jaeger, before Loki
app.MapGet("/simulate", async (ILogger<Program> logger) =>
{
    var orderId   = Random.Shared.Next(1000, 9999);
    var gatewayMs = Random.Shared.Next(800, 2000);

    // WithPiiMasking() strips "customer@example.com" → "***@***.***"
    // before OtlpLogSink.EmitAsync is called — PII never reaches OTLP endpoint
    logger.LogInformation("Payment started for {OrderId} by {Email}",
        orderId, "customer@example.com");
    Activity.Current?.AddEvent(new ActivityEvent("payment-started", tags:
        new ActivityTagsCollection { ["order_id"] = orderId, ["email"] = "***@***.***" }));

    await Task.Delay(30);

    logger.LogWarning("Gateway slow: {GatewayMs}ms for order {OrderId}", gatewayMs, orderId);
    Activity.Current?.AddEvent(new ActivityEvent("gateway-slow", tags:
        new ActivityTagsCollection { ["gateway_ms"] = gatewayMs, ["order_id"] = orderId }));

    logger.LogInformation("Payment confirmed for order {OrderId}", orderId);
    Activity.Current?.AddEvent(new ActivityEvent("payment-confirmed", tags:
        new ActivityTagsCollection { ["order_id"] = orderId }));

    return Results.Ok(new { status = "Payment processed", orderId });
});

// Exception → exception.type + exception.stacktrace in OTel log record + error span
app.MapGet("/error", (ILogger<Program> logger) =>
{
    try
    {
        throw new InvalidOperationException("Downstream payment service timeout");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "PaymentGateway failed for environment {Env}", env);
        Activity.Current?.AddException(ex);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
    }
    return Results.Problem("Payment service unavailable", statusCode: 503);
});

// Warning for slow response — visible in Jaeger span timeline duration
app.MapGet("/slow", async (ILogger<Program> logger) =>
{
    var ms = Random.Shared.Next(100, 1500);
    await Task.Delay(ms);
    logger.LogWarning("Slow response detected — {ActualMs}ms", ms);
    return Results.Ok(new { status = "done", actualMs = ms });
});

app.Run();
