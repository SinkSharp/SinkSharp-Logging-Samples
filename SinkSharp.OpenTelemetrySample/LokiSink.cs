using System.Net.Http.Json;
using System.Text.Json;
using SinkSharp.Core;
using SinkSharp.Core.Abstractions;

/// <summary>
/// SinkSharp sink that pushes log events directly to Grafana Loki
/// via its JSON push API (<c>/loki/api/v1/push</c>).
///
/// <para>
/// <see cref="LokiSink"/> is the simplest way to get SinkSharp logs into Loki —
/// no OTel Collector required. All SinkSharp enrichments (CorrelationId, PII masking,
/// UserId, RequestPath, ElapsedMs, Environment, MachineName) are already stamped on
/// the <see cref="LogEvent"/> before <see cref="EmitAsync"/> is called, so every log
/// line in Loki carries the full structured context automatically.
/// </para>
///
/// <para><b>Usage:</b></para>
/// <code>
/// builder.Services.AddSinkSharp(options => options
///     .WithCorrelationId()
///     .WithEnvironment("MyApp", env)
///     .WithPiiMasking()
///     .WriteTo(new ColoredConsoleSink(),              LogLevel.Debug)
///     .WriteTo(new LokiSink("http://localhost:3100"), LogLevel.Information));
/// </code>
///
/// <para><b>Loki labels assigned per event:</b></para>
/// <list type="bullet">
/// <item><c>service_name</c> — from the <c>AppName</c> enricher property</item>
/// <item><c>level</c> — log level in lowercase (information, warning, error …)</item>
/// <item><c>environment</c> — from the <c>Environment</c> enricher property</item>
/// </list>
///
/// <para>
/// The log line body is the rendered message followed by a JSON object containing
/// all structured properties — queryable in Grafana with <c>| json</c> pipeline.
/// </para>
///
/// <para><b>Batching:</b> events are buffered and flushed every 10 events or on
/// <see cref="FlushAsync"/>. The sink flushes automatically on <see cref="DisposeAsync"/>
/// so no events are lost at graceful shutdown.</para>
/// </summary>
public sealed class LokiSink : ISink, IAsyncDisposable
{
    private readonly HttpClient  _http;
    private readonly string      _pushUrl;
    private readonly List<(string ts, string line, Dictionary<string, string> labels)> _buffer = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private          bool          _disposed;

    /// <inheritdoc/>
    public string Name => "Loki";

    /// <summary>
    /// Creates a <see cref="LokiSink"/> that pushes to the specified Loki instance.
    /// </summary>
    /// <param name="lokiUrl">
    /// Base URL of the Loki instance. Default: <c>http://localhost:3100</c>.
    /// In Kubernetes use the service DNS name, e.g. <c>http://loki.monitoring.svc:3100</c>.
    /// </param>
    public LokiSink(string lokiUrl = "http://localhost:3100")
    {
        _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _pushUrl = $"{lokiUrl.TrimEnd('/')}/loki/api/v1/push";
    }

    /// <inheritdoc/>
    public async ValueTask EmitAsync(LogEvent e, CancellationToken ct = default)
    {
        var ts = e.Timestamp.ToUniversalTime().ToUnixTimeNanoseconds().ToString();

        // Loki requires low-cardinality labels — use stable, bounded values only.
        // High-cardinality values like CorrelationId belong in the log line body (below).
        var labels = new Dictionary<string, string>
        {
            ["service_name"] = e.Properties.TryGetValue("AppName", out var app)
                                    ? app?.ToString() ?? "SinkSharp"
                                    : "SinkSharp",
            ["level"]        = e.Level.ToString().ToLowerInvariant(),
            ["environment"]  = e.Properties.TryGetValue("Environment", out var env)
                                    ? env?.ToString() ?? "Development"
                                    : "Development",
        };

        // Log line: rendered message + all structured properties as queryable JSON.
        // Use | json in Grafana to filter/aggregate by any of these fields.
        var props = new Dictionary<string, object?>(e.Properties)
        {
            ["level"]         = e.Level.ToString(),
            ["category"]      = e.Category,
            ["correlationId"] = e.CorrelationId,
        };
        if (e.Exception is not null)
            props["exception"] = e.Exception.ToString();

        var line = $"{e.RenderedMessage ?? e.MessageTemplate} | {JsonSerializer.Serialize(props)}";

        await _lock.WaitAsync(ct);
        try
        {
            _buffer.Add((ts, line, labels));
            if (_buffer.Count >= 10)
                await FlushInternalAsync(ct);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc/>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { await FlushInternalAsync(ct); }
        finally { _lock.Release(); }
    }

    private async Task FlushInternalAsync(CancellationToken ct)
    {
        if (_buffer.Count == 0) return;

        // Group events by their label set — Loki push API accepts multiple streams per request.
        var streams = _buffer
            .GroupBy(e => string.Join(",",
                e.labels.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")))
            .Select(g => new
            {
                stream = g.First().labels,
                values = g.Select(e => new[] { e.ts, e.line }).ToArray()
            })
            .ToArray();

        var payload = new { streams };
        try
        {
            var resp = await _http.PostAsJsonAsync(_pushUrl, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[LokiSink] Push failed: {resp.StatusCode} — {body}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LokiSink] Push error: {ex.Message}");
        }
        finally { _buffer.Clear(); }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await FlushAsync();   // flush remaining buffered events before shutdown
        _http.Dispose();
        _lock.Dispose();
    }
}

/// <summary>Extension helper — <see cref="DateTimeOffset"/> to Unix nanoseconds for Loki timestamps.</summary>
internal static class DateTimeOffsetExtensions
{
    internal static long ToUnixTimeNanoseconds(this DateTimeOffset dt)
        => dt.ToUnixTimeMilliseconds() * 1_000_000L;
}
