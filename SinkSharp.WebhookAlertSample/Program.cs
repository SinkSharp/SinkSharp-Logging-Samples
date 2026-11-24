// Manual smoke test — posts real events to a real Slack and/or Teams webhook, exercising
// every option on both sinks. Not part of the test suite; run directly against a webhook
// URL you created (see README.md in this folder for how to get one of each).
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SinkSharp.Core;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Slack;
using SinkSharp.Sinks.MicrosoftTeams;

var slackUrl = Environment.GetEnvironmentVariable("SLACK_WEBHOOK_URL");
var teamsUrl = Environment.GetEnvironmentVariable("TEAMS_WEBHOOK_URL");

// Print what this process actually sees for each variable — the #1 cause of "nothing
// arrived" is a mismatch between where you set the variable and where you ran this from
// (a PowerShell $env: assignment doesn't carry into a separate Visual Studio debug run;
// Visual Studio reads Properties/launchSettings.json instead). This line removes the guesswork.
Console.WriteLine($"SLACK_WEBHOOK_URL: {Mask(slackUrl)}");
Console.WriteLine($"TEAMS_WEBHOOK_URL: {Mask(teamsUrl)}");

if (slackUrl is null && teamsUrl is null)
{
    Console.WriteLine("Neither variable is set in THIS process — set one and run again.");
    Console.WriteLine("Running via `dotnet run`: set it in the same terminal, right before the command.");
    Console.WriteLine("Running via Visual Studio (F5): fill it in under Properties/launchSettings.json instead.");
    return;
}

static string Mask(string? url) =>
    url is null ? "(not set)" : url.Length > 20 ? $"{url[..20]}... ({url.Length} chars)" : "(set, but looks too short — check it was pasted in full)";

// A single HttpClient shared by both sinks — the recommended pattern once you're past a
// quick local test. In an ASP.NET Core app, get this from IHttpClientFactory instead of
// `new HttpClient()` so DNS changes and socket reuse are handled for you.
using var sharedHttpClient = new HttpClient
{
    DefaultRequestHeaders = { UserAgent = { ProductInfoHeaderValue.Parse("SinkSharp-WebhookAlertSample/1.0") } },
};

var builder = SinkSharpBuilder.Create()
    .MinimumLevel(LogLevel.Information)
    .WriteTo(new ColoredConsoleSink());

if (slackUrl is not null)
{
    builder = builder.WriteTo(new SlackWebhookSink(new SlackWebhookSinkOptions
    {
        WebhookUrl       = slackUrl,
        Channel          = null,                 // leave unset — most workspaces ignore this override anyway
        Username         = "SinkSharp Sample",    // bot display name
        IconEmoji        = ":satellite:",         // bot avatar
        Timeout          = TimeSpan.FromSeconds(8),
        MaxRetries       = 2,                     // one more than the default — this sample is noisier than production should be
        IncludeException = true,
        MaxFieldLength   = 1500,                  // shorter than the 2800 default, just to show truncation below
        HttpClient       = sharedHttpClient,       // Timeout above is then ignored — set it on sharedHttpClient instead if you need one
    }), LogLevel.Error);         // only Error and Critical reach Slack
}

if (teamsUrl is not null)
{
    builder = builder.WriteTo(new TeamsWebhookSink(new TeamsWebhookSinkOptions
    {
        WebhookUrl       = teamsUrl,
        Timeout          = TimeSpan.FromSeconds(8),
        MaxRetries       = 2,
        IncludeException = true,
        MaxFieldLength   = 1500,
        HttpClient       = sharedHttpClient,
    }), LogLevel.Error);         // only Error and Critical reach Teams
}

var logger = builder.BuildLogger("WebhookSmokeTest");

logger.Log(LogLevel.Information, "This one stays local — only Console sees it");

// 1) A normal alert with an exception attached.
try
{
    throw new InvalidOperationException("Simulated failure for the webhook smoke test");
}
catch (Exception ex)
{
    logger.Log(LogLevel.Error, ex, "Order {OrderId} failed to process", 42);
}

// 2) A Critical event, to see the badge/color change (🔴 vs ⚠️).
logger.Log(LogLevel.Critical, "Payment gateway unreachable — all checkouts are failing");

// 3) A long message, to see MaxFieldLength truncation in action (set to 1500 above).
logger.Log(LogLevel.Error, "Bulk validation failed: {Details}", new string('x', 2000));

// Wait for every enqueued event to actually finish processing — including the Slack/Teams
// HTTP POST itself — rather than guessing with a fixed delay. A plain Task.Delay can let the
// process exit while the webhook call is still mid-flight (slower on a first connection:
// DNS, TLS handshake, JIT warm-up), silently dropping it even though nothing failed. This
// drains the queue for real (up to 30s) and only returns once every sink has caught up.
Console.WriteLine("Flushing — waiting for the webhook call(s) to complete...");
await builder.GetQueue().DrainAndStopAsync();
Console.WriteLine("Done — check Slack/Teams for three alerts (exception, critical, truncated).");
