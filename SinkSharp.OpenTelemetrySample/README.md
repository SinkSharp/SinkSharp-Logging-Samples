# SinkSharp.OpenTelemetrySample

Demonstrates how to export SinkSharp log events to any OpenTelemetry-compatible backend
via the OTLP protocol — Jaeger, Grafana Loki, Datadog, Azure Monitor, or Elasticsearch.

---

## Why OpenTelemetry?

OpenTelemetry (OTel) is a **universal wire format** for observability data. Once you emit
OTLP, you can route to any backend by changing collector config — no code changes needed.

```
Your app (SinkSharp + OtlpLogSink)
        │
        │  OTLP  (gRPC port 4317  or  HTTP port 4318)
        ▼
  OTel Collector / Jaeger / Datadog Agent
        │
        ├─→ Jaeger UI           (local dev — zero config)
        ├─→ Grafana + Loki      (team environments)
        ├─→ Datadog APM + Logs  (cloud-native)
        ├─→ Azure Monitor       (Azure shops)
        └─→ Elasticsearch       (self-hosted)
```

**Key benefit for SinkSharp:** all enrichers (PII masking, CorrelationId, user context,
environment) run **before** OtlpLogSink sees the event. Your OTel backend receives
fully-enriched, PII-free events with no extra config on the collector side.

---

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) — for local backends

---

## Quick start — Option 1: Jaeger (recommended for local dev)

**Fastest start — one command, no config, great UI.**

### Step 1 — Start Jaeger

```bash
cd samples/SinkSharp.OpenTelemetrySample
docker compose up -d jaeger
```

### Step 2 — Run the sample

```bash
dotnet run --launch-profile Jaeger
# OR: set OTEL_BACKEND=jaeger and dotnet run
```

### Step 3 — Open Jaeger UI

```
http://localhost:16686
```

In the Jaeger UI:
1. **Service** dropdown → select `SinkSharp.OpenTelemetrySample`
2. Click **Find Traces** → you'll see log events as spans
3. Click any span → see all SinkSharp properties as OTel attributes

### Step 4 — Generate log events

```bash
# Hit sample endpoints to produce varied log events
curl http://localhost:5200/orders
curl http://localhost:5200/orders/42
curl http://localhost:5200/simulate     # PII masking demo
curl http://localhost:5200/error        # exception.stacktrace in OTel
curl http://localhost:5200/slow         # warning for slow responses
```

---

## Quick start — Option 2: Grafana + Loki (team environments)

**Full observability stack — Loki for log storage, Grafana for dashboards.**

### Step 1 — Start the full stack

```bash
docker compose --profile grafana up -d
# Starts: OTel Collector + Loki + Grafana
# Wait ~10 seconds for all services to be ready
```

### Step 2 — Run the sample

```bash
dotnet run --launch-profile Grafana
```

### Step 3 — Open Grafana

```
http://localhost:3000
```

Credentials: `admin` / `admin` (skip the change-password prompt)

In Grafana:
1. Left sidebar → **Explore** (compass icon)
2. Datasource → **Loki** (pre-configured automatically)
3. Label filters → `service_name` = `SinkSharp.OpenTelemetrySample`
4. Click **Run query** → see live log stream
5. Click any line → expand to see all OTel attributes (CorrelationId, UserId, etc.)

### How the routing works

```
dotnet run → SinkSharp → OTLP gRPC :4317
                                    │
                              OTel Collector (otel-collector.yaml)
                                    │
                                    ▼
                                  Loki :3100
                                    │
                                    ▼
                              Grafana :3000
```

---

## Quick start — Option 3: Datadog (cloud, no Docker needed)

**Best for teams already using Datadog or wanting cloud-native observability.**

### Step 1 — Get your API key

Log in to [app.datadoghq.com](https://app.datadoghq.com) → **Organization Settings** → **API Keys** → copy your key.

### Step 2 — Run the sample

```powershell
# PowerShell
$env:DD_API_KEY = "your-datadog-api-key"
dotnet run --launch-profile Datadog
```

```bash
# Bash / Mac
DD_API_KEY=your-datadog-api-key dotnet run --launch-profile Datadog
```

### Step 3 — Open Datadog Logs

```
https://app.datadoghq.com/logs
```

Filter: `service:SinkSharp.OpenTelemetrySample`

You'll see all SinkSharp structured properties as Datadog facets.

---

## What you will see in each backend

After hitting `/simulate`:

```
SinkSharp log event → OTel LogRecord attributes:
┌─────────────────────────────────────────────────────┐
│ severity:          Information                       │
│ body:              Payment started for 4231 by cu... │
│ log.record.original: Payment started for {OrderId}.. │
│ code.namespace:    SinkSharp.OpenTelemetrySample     │
│ OrderId:           4231                              │
│ Email:             customer@***    ← PII masked ✅   │
│ CorrelationId:     abc123def456                      │
│ correlation.id:    abc123def456                      │
│ RequestId:         0HNPKRQ1ABCD:0001                 │
│ HttpMethod:        GET                               │
│ RequestPath:       /simulate                         │
│ ElapsedMs:         34                                │
│ AppName:           SinkSharp.OpenTelemetrySample     │
│ Environment:       Development                       │
│ MachineName:       YOUR-PC                           │
└─────────────────────────────────────────────────────┘
```

After hitting `/error`:

```
severity:              Error
exception.type:        System.InvalidOperationException
exception.message:     Downstream payment service timeout
exception.stacktrace:  at SinkSharp.OpenTelemetrySample...
```

---

## LogEvent → OTel LogRecord mapping

| SinkSharp field | OTel LogRecord field | Notes |
|---|---|---|
| `Level` | `SeverityNumber` + `SeverityText` | Debug=5, Info=9, Warning=13, Error=17, Critical=21 |
| `RenderedMessage` | `Body` | Fully rendered message |
| `MessageTemplate` | Attribute: `log.record.original` | Raw template for log parsing |
| `Category` | Attribute: `code.namespace` | Logger category name |
| `Properties[]` | Attributes (all key/value) | All enriched + structured properties |
| `Exception.Message` | Attribute: `exception.message` | |
| `Exception.StackTrace` | Attribute: `exception.stacktrace` | |
| `Timestamp` | `Timestamp` | ISO 8601 UTC |
| `CorrelationId` | Attribute: `correlation.id` | Also `trace.id` if 32-char hex |

---

## Using `SinkSharp.Sinks.OpenTelemetry` (once published)

This sample currently uses the raw OpenTelemetry SDK for compatibility.
Once `SinkSharp.Sinks.OpenTelemetry` is published to NuGet, the setup simplifies to:

```bash
dotnet add package SinkSharp.Core --version 1.0.9
dotnet add package SinkSharp.Sinks.OpenTelemetry --version 1.0.0
```

```csharp
builder.Services.AddSinkSharp(options => options
    .WithCorrelationId()
    .WithEnvironment("MyApp", env)
    .WithPiiMasking()
    .WriteTo(new ColoredConsoleSink(), LogLevel.Debug)
    .WriteTo(new OtlpLogSink(new OtlpLogSinkOptions
    {
        Endpoint              = new Uri("http://localhost:4317"),
        Protocol              = OtlpExportProtocol.Grpc,
        ServiceName           = "MyApp",
        ServiceVersion        = "1.0.0",
        DeploymentEnvironment = env,
        // Datadog API key auth:
        // Headers = new Dictionary<string, string> { ["DD-API-KEY"] = config["DD_API_KEY"] }
        BatchSize             = 512,
        FlushInterval         = TimeSpan.FromSeconds(5),
    })));
```

No `builder.Logging.AddOpenTelemetry()` needed — the sink handles OTLP export directly.

---

## Backends comparison

| Backend | Docker needed? | Account needed? | Best for |
|---|---|---|---|
| **Jaeger** | ✅ Yes (1 container) | ❌ No | Local dev, trace correlation |
| **Grafana + Loki** | ✅ Yes (3 containers) | ❌ No | Team environments, dashboards |
| **Datadog** | ❌ No | ✅ Yes (free trial) | Cloud-native, APM + logs + metrics |
| **Azure Monitor** | ❌ No | ✅ Yes (Azure) | Azure-native apps |
| **Elasticsearch** | ✅ Yes | ❌ No | Self-hosted, full-text search |

---

## Switching backends without code changes

The OTel Collector acts as a router. With one collector config change, you can
send to multiple backends simultaneously — no application code changes:

```yaml
# otel-collector.yaml — add or remove exporters freely
service:
  pipelines:
    logs:
      receivers:  [otlp]
      processors: [batch]
      exporters:  [loki, datadog, elasticsearch]   # all three at once
```

---

## Stop local backends

```bash
# Stop Jaeger only
docker compose stop jaeger

# Stop Grafana stack
docker compose --profile grafana down

# Remove all containers and volumes
docker compose --profile grafana down -v
```

---

## Also in this sample folder

| File | Purpose |
|---|---|
| `docker-compose.yml` | Jaeger + Grafana/Loki stack |
| `otel-collector.yaml` | OTel Collector routing config (Loki exporter) |
| `grafana-datasources.yaml` | Auto-provisions Loki as Grafana datasource |
| `Properties/launchSettings.json` | VS launch profiles: Jaeger / Grafana / Datadog |

---

## Release notes

### v1.0.0
- Demonstrates OTLP export from SinkSharp to Jaeger, Grafana Loki, and Datadog
- `OTEL_BACKEND` env var / launch profile selects backend at startup — no code changes
- Docker Compose with Jaeger profile (1 container) and Grafana profile (3 containers)
- OTel Collector config pre-wired to route logs to Loki with SinkSharp label mapping
- Shows PII masking, CorrelationId, user context, exception attributes in OTel backends

## Links

| | |
|---|---|
| 🌐 Website | [https://www.sinksharp.com](https://www.sinksharp.com) |
| 📦 NuGet | [nuget.org/profiles/sinksharp](https://www.nuget.org/profiles/sinksharp) |
| 🔭 All samples | [SinkSharp-Logging-Samples](https://github.com/SinkSharp/SinkSharp-Logging-Samples) |
| 📧 Support | [support@sinksharp.com](mailto:support@sinksharp.com) |
| 📄 Licence | [Apache 2.0](https://github.com/SinkSharp/.github/blob/main/profile/LICENSE) |
