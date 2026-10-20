using Microsoft.Extensions.DependencyInjection;
// SinkSharp — Worker Service Sample
// Dashboard runs on its own port (no HTTP pipeline in Worker Service)
// Run: dotnet run
// Open: http://localhost:5341

using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.Dashboard.Server;
using SinkSharp.Sinks.File;

var builder = Host.CreateApplicationBuilder(args);

// ── SinkSharp setup ───────────────────────────────────────────
DashboardSink dashSink = null!;

builder.Services.AddSinkSharp(options => options
    .MinimumLevel(LogLevel.Information)
    .WithCorrelationId()
    .WithEnvironment("SinkSharp.Worker", builder.Environment.EnvironmentName)
    .WithMachineName()
    // Drop routine heartbeat messages — keep logs actionable
    .Filter(e => !e.MessageTemplate.Contains("heartbeat", StringComparison.OrdinalIgnoreCase))
    // Dashboard: Information+ (matches global min — no extra filter needed)
    .WithDashboard(out dashSink)
    // Console: Information+ (matches global min)
    .WriteTo(new ColoredConsoleSink())
    // File: Warning+ — worker files stay small; errors and warnings only
    .WriteTo(new RollingFileSink("logs", "SinkSharp.Worker"), LogLevel.Warning));

builder.Services.AddHostedService<OrderProcessingWorker>();
builder.Services.AddHostedService<ReportGenerationWorker>();

var host = builder.Build();

// ── Standalone dashboard on its own port ──────────────────────
// Worker Service has no HTTP pipeline so we use an embedded
// Kestrel server that serves just the dashboard routes.
// http://localhost:5341       ← UI
// http://localhost:5341/hub   ← SignalR
// http://localhost:5341/stats ← JSON stats
// Pass the queue so dashboard stats show real numbers
// (DashboardServer has its own embedded DI — cannot resolve from host)
var queue = host.Services.GetRequiredService<SinkSharp.Core.Queue.AsyncLogQueue>();
var dashboard = new DashboardServer(dashSink, port: 5341, queue: queue);
await dashboard.StartAsync();

await host.RunAsync();
await dashboard.DisposeAsync();

// ── Workers ───────────────────────────────────────────────────
public class OrderProcessingWorker(ILogger<OrderProcessingWorker> logger)
    : BackgroundService
{
    private static readonly string[] Statuses = ["Pending", "Processing", "Shipped", "Delivered"];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Order processing worker started");

        while (!ct.IsCancellationRequested)
        {
            // No app.UseSinkSharp() here — Worker Service has no HTTP pipeline.
            // Set correlation ID manually per job using CorrelationContext.Use().
            using var _ = CorrelationContext.Use(Guid.NewGuid().ToString("N")[..8]);

            var orderId = Random.Shared.Next(1000, 9999);
            var status  = Statuses[Random.Shared.Next(Statuses.Length)];

            logger.LogInformation(
                "Processing order {OrderId} → {Status}", orderId, status);

            if (status == "Pending")
                logger.LogWarning(
                    "Order {OrderId} stuck in Pending for {Hours}h", orderId, 3);

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }
}

public class ReportGenerationWorker(ILogger<ReportGenerationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Report generation worker started");

        while (!ct.IsCancellationRequested)
        {
            logger.LogInformation(
                "Generating daily report for {Date:yyyy-MM-dd}", DateTime.UtcNow);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            logger.LogInformation("Report generated successfully");
        }
    }
}
