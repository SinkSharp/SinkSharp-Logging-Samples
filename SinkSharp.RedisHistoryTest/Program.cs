using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Core.Middleware;
using SinkSharp.Sinks.Dashboard;

var builder = WebApplication.CreateBuilder(args);
var pod = Environment.GetEnvironmentVariable("POD") ?? "pod?";
var redis = "localhost:6379";

DashboardSink dashSink = null!;
builder.Services.AddSinkSharp(o => o
    .MinimumLevel(LogLevel.Debug)
           .MinimumLevelFor("Microsoft", LogLevel.Warning)   // add this

    .WithEnvironment(pod, "Test")
    .WithDashboard(out dashSink));

// Pub/Sub backplane (live) + Streams (history)
builder.Services.AddSinkSharpDashboardRedis(dashSink,
    signalR => signalR.AddStackExchangeRedis(redis, o =>
        o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("SinkSharpTest")));
    //,    streams => { streams.ConnectionString = redis; streams.RetentionDays = 7; });

var app = builder.Build();
app.UseSinkSharp();
app.UseSinkSharpDashboard("/sinksharp/dashboard");

app.MapGet("/log", (ILogger<Program> log, int count = 1) =>
{
    for (var i = 1; i <= count; i++)
        log.LogInformation("Order {OrderId} handled by {Pod}", i, pod);
    log.LogError("Payment failed on {Pod}", pod);
    return $"{pod}: logged {count + 1}";
});

app.Run();