# SinkSharp.AdvancedEnricherSample

Demonstrates all v1.1 enricher and pipeline features in one runnable project.

## Run

```bash
dotnet run
```

Open `https://localhost:5001/sinksharp/dashboard` to see the live log stream.

## What this shows

| Feature | API |
|---|---|
| Machine name standalone | `.WithMachineName()` |
| ASP.NET request ID | `.AddSinkSharpRequestId()` → stamps `RequestId` |
| Authenticated user | `.AddSinkSharpUserContext()` → stamps `UserId`, `UserName`, `UserRole` |
| HTTP method + path | `.AddSinkSharpRequestPath()` → stamps `HttpMethod`, `RequestPath` |
| Elapsed request time | `.AddSinkSharpElapsedTime()` + `app.UseSinkSharp()` → stamps `ElapsedMs` |
| Structured event filter | `.Filter(e => !e.MessageTemplate.Contains("health"))` |
| Conditional enrichment | `.WithEnricher(enricher, when: e => e.Level >= Warning)` |
| Runtime level change | `SinkSharpLoggerFactory.SetGlobalLevel(LogLevel.Debug)` |
| Per-sink minimum level | `.WriteTo(sink, LogLevel.Warning)` |
| Correlation middleware | `app.UseSinkSharp()` |

## Endpoints

| Endpoint | What to observe |
|---|---|
| `GET /orders` | Debug + Info events — Debug goes to console only |
| `GET /orders/42` | Info + structured `OrderId` property |
| `GET /orders/101` | Warning with `TimestampEnricher.UtcTimestamp` stamped |
| `GET /orders/fail` | Error with exception + PII masked email |
| `GET /health-check` | No log output — filtered before enqueue |
| `PUT /debug/level/Debug` | Enable debug logs at runtime |
| `PUT /debug/level/None` | Restore startup level |
| `GET /debug/level` | Show current runtime level |
