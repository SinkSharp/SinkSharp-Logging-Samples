# SinkSharp.RedisHistoryTest

A small app for trying the dashboard with a Redis backplane (live events across pods) and Redis Streams history (shared, kept across restarts). Run it twice on two ports to act as two pods.

## Run

```powershell
docker run -d --name sinksharp-redis -p 6379:6379 redis:7

# terminal 1
$env:POD="podA"; dotnet run --no-launch-profile --urls http://localhost:5001
# terminal 2
$env:POD="podB"; dotnet run --no-launch-profile --no-build --urls http://localhost:5002
```

`GET /log?count=20` on either pod writes 20 information events and one error.

## What to check

| Do this | You should see |
|---|---|
| Hit `5001/log?count=20`, open the dashboard on 5002 | podA's events; footer reads `History: Redis Streams` |
| Keep 5002's dashboard open, hit `5001/log` | New rows appear without a refresh (backplane) |
| Stop both pods, start one, open its dashboard | Earlier events are still there (Streams) |
| Hit `/log?count=1200`, refresh, click **Load older** | 500 rows, then 500 more per click |
| `docker stop sinksharp-redis`, hit `/log`, refresh | Pod's buffered events after about 5 s, a "Live stream unavailable" note, pill reads "Reconnecting…" |
| `docker start sinksharp-redis`, do not refresh | Within about 30 s the note clears and the pill returns to "Live" |

## Notes

- The project targets the SDK's default framework. Pin `Microsoft.AspNetCore.SignalR.StackExchangeRedis` to the matching major version (for example `9.0.*` on net9.0).
- Useful Redis commands: `XLEN sinksharp:dashboard:logs`, `XREVRANGE sinksharp:dashboard:logs + - COUNT 1`, `PUBSUB CHANNELS "*"`, and `DEL sinksharp:dashboard:logs` to start clean.
