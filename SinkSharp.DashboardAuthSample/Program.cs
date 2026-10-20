using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SinkSharp.Core;
using SinkSharp.Core.Enrichers;
using SinkSharp.Sinks.Console;
using SinkSharp.Sinks.Dashboard;
using SinkSharp.Sinks.File;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ──────────────────────────────────────────────────────────────────────────────
// 1. Configure SinkSharp
// ──────────────────────────────────────────────────────────────────────────────
DashboardSink dashSink = null!;

builder.Services.AddSinkSharp(options => options
    .WithCorrelationId()
    .WithEnvironment("AuthSample", builder.Environment.EnvironmentName)
    .WithDashboard(out dashSink)
    .WriteTo(new ColoredConsoleSink())
    .WriteTo(new RollingFileSink("logs", "AuthSample")));

builder.Services.AddSinkSharpDashboard(dashSink);

// ──────────────────────────────────────────────────────────────────────────────
// 2. Configure JWT authentication (required for bearer modes)
// ──────────────────────────────────────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? "dev-secret-key")),
            ValidateIssuer           = false,
            ValidateAudience         = false,
        };
        // SignalR sends the token via query string — map it to the header
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/sinksharp/dashboard/hub"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
    opts.AddPolicy("DashboardAccess", p =>
        p.RequireAssertion(ctx =>
            ctx.User.HasClaim("permission", "dashboard:read")));
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// ──────────────────────────────────────────────────────────────────────────────
// 3. Choose ONE of the auth modes below — comment out the others
// ──────────────────────────────────────────────────────────────────────────────

// MODE 1: No auth — open dashboard (local dev, VPN-protected internal tools)
app.UseSinkSharpDashboard("/sinksharp/dashboard");

// MODE 2: Bearer JWT — any authenticated user
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.RequireAuthorization());

// MODE 3: Named policy — must have "AdminOnly" policy configured above
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.RequireAuthorization("AdminOnly"));

// MODE 4: Custom permission attribute — must have "DashboardAccess" policy
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.RequireAuthorization("DashboardAccess"));

// MODE 5: Role check — inline, no named policy needed
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.RequireRole("Admin", "DevOps"));

// MODE 6: Shared token — no AddAuthentication() required
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.UseToken(builder.Configuration["Dashboard:Token"]));
// Store in appsettings: "Dashboard": { "Token": "your-secret-here" }

// MODE 7: IP allowlist — office / VPN ranges
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.AllowFrom("10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12"));

// MODE 8: HTTP Basic auth — browser username/password prompt
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.UseBasicAuth("admin", builder.Configuration["Dashboard:Password"]));

// MODE 9: Full control — rate limiting, CORS, any endpoint middleware
// app.UseSinkSharpDashboard("/sinksharp/dashboard", opts =>
//     opts.ConfigureEndpoints(ep =>
//     {
//         ep.RequireRateLimiting("fixed");
//         ep.RequireCors("DashboardPolicy");
//     }));

// ──────────────────────────────────────────────────────────────────────────────
// 4. Sample endpoints — generate some logs to see in the dashboard
// ──────────────────────────────────────────────────────────────────────────────
app.MapGet("/", (ILogger<Program> log) =>
{
    log.LogInformation("Home page hit");
    return Results.Ok("SinkSharp Dashboard Auth Sample — open /sinksharp/dashboard");
});

app.MapGet("/warn", (ILogger<Program> log) =>
{
    log.LogWarning("Warning from sample endpoint");
    return Results.Ok("Warning logged");
});

app.MapGet("/error", (ILogger<Program> log) =>
{
    try { throw new InvalidOperationException("Simulated error"); }
    catch (Exception ex) { log.LogError(ex, "Error from sample endpoint"); }
    return Results.Ok("Error logged");
});

app.MapGet("/pii", (ILogger<Program> log) =>
{
    // PII enricher will mask these in the dashboard
    log.LogInformation("User alice@example.com placed order with card 4111111111111111");
    return Results.Ok("PII log written — check the dashboard to see masking");
});

app.MapGet("/corr", (ILogger<Program> log) =>
{
    CorrelationContext.Current = Guid.NewGuid().ToString("N")[..8];
    log.LogInformation("Step 1: validating order");
    log.LogInformation("Step 2: charging payment");
    log.LogInformation("Step 3: sending confirmation");
    return Results.Ok("Correlated logs written — click the correlation ID in the dashboard to filter");
});

app.Run();
