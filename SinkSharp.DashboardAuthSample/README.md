# SinkSharp.DashboardAuthSample

Shows all 8 dashboard authentication modes in a single runnable project.

## Run

```bash
cd samples/SinkSharp.DashboardAuthSample
dotnet run
```

Open http://localhost:5000/sinksharp/dashboard

Then visit these endpoints to generate logs:
- `GET /` — info log
- `GET /warn` — warning log  
- `GET /error` — error with exception
- `GET /pii` — PII-masked log (email + card)
- `GET /corr` — correlated request across 3 log events

## Switch auth mode

In `Program.cs`, comment out Mode 1 (no auth) and uncomment the mode you want to test.
All modes are documented with the exact method to call on `UseSinkSharpDashboard`.
