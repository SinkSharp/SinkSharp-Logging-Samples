// SinkSharp — Console App Sample
// Dashboard runs on its own port (no HTTP pipeline in console apps)
// Run: dotnet run
// Open: http://localhost:5341

using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.Dashboard.Server;
using SinkSharp.Sinks.File;

// ── SinkSharp setup (no DI needed for console apps) ──────────
//
// Per-sink minimum levels keep each destination at the right verbosity:
//   Console  : Debug+        — see everything while developing
//   File     : Warning+      — production log files stay small
//   Dashboard: Information+  — no Debug noise in the real-time UI
//
var builder = SinkSharpBuilder
    .Create()
    .MinimumLevel(LogLevel.Debug)            // global gate — process nothing below Debug
    .WithCorrelationId()
    .WithEnvironment("SinkSharp.Console", "Development")
    .WithPiiMasking()
    .WithDashboard(out var dashSink, minLevel: LogLevel.Information)
    .WriteTo(new ColoredConsoleSink(),              LogLevel.Debug)
    .WriteTo(new RollingFileSink("logs", "SinkSharp.Console"), LogLevel.Warning);

var logger = builder.BuildLogger("SinkSharp.Console");

// ── Standalone dashboard ──────────────────────────────────────
// http://localhost:5341       ← UI
// http://localhost:5341/hub   ← SignalR
// http://localhost:5341/stats ← JSON stats
var dashboard = new DashboardServer(dashSink, port: 5341, queue: builder.GetQueue());
await dashboard.StartAsync();
await dashboard.Ready;

Console.WriteLine("SinkSharp Console Sample running");
Console.WriteLine("Dashboard → http://localhost:5341");
Console.WriteLine("  Console : Debug+");
Console.WriteLine("  File    : Warning+ (logs/SinkSharp.Console-*.jsonl)");
Console.WriteLine("  Dashboard: Information+");
// Note: app.UseSinkSharp() is for ASP.NET Core Web API / Minimal API only.
// In console apps, set correlation ID manually per logical operation:
Console.WriteLine("Press Ctrl+C to stop\n");

// ── Simulate real app activity ────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var batch = 0;
while (!cts.IsCancellationRequested)
{
    batch++;
    using var _ = CorrelationContext.Use($"batch-{batch:D4}");

    // Debug → console only
    logger.Log(LogLevel.Debug,
        "Starting batch {Batch} at {Time:HH:mm:ss}", batch, DateTime.UtcNow);

    for (var i = 1; i <= 5; i++)
    {
        // Debug → console only (file + dashboard skip Debug)
        logger.Log(LogLevel.Debug,
            "Processing item {Item} of {Total} in batch {Batch}", i, 5, batch);
        await Task.Delay(300, cts.Token).ContinueWith(_ => { });
    }

    // Information → console + dashboard (file skips Info)
    logger.Log(LogLevel.Information,
        "Batch {Batch} complete — processed {Count} items", batch, 5);

    if (batch % 3 == 0)
        // Warning → all sinks: console, file, dashboard
        logger.Log(LogLevel.Warning,
            "Batch {Batch} took longer than expected ({Ms}ms)", batch, 1600);

    if (batch % 7 == 0)
        // Error → all sinks (unbounded channel — never dropped)
        logger.Log(LogLevel.Error,
            "Failed to save batch {Batch} result for user {Email}",
            batch, "ops@example.com");
        // → PII masked: "ops@***"

    await Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ContinueWith(_ => { });
}

await dashboard.DisposeAsync();
Console.WriteLine("Stopped.");
