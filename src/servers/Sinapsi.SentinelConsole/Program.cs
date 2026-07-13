using Sinapsi.Nats;
using Sinapsi.SentinelConsole;

var builder = WebApplication.CreateBuilder(args);

// The read-model + live feed are process singletons; the bus subscriber fills them.
builder.Services.AddSingleton(new ReadModel(
    capacity: EnvInt("SENTINEL_CONSOLE_BUFFER", 2000)));
builder.Services.AddSingleton<LiveFeed>();
builder.Services.AddSingleton(NatsConnectionOptions.FromEnvironment() with
{
    ClientName = "sinapsi-sentinel-console",
});
builder.Services.AddHostedService<SecurityBusSubscriber>();
// Dev-only populated view (no live bus) when SENTINEL_CONSOLE_DEMO=1.
if (Environment.GetEnvironmentVariable("SENTINEL_CONSOLE_DEMO") == "1")
    builder.Services.AddHostedService<DemoSeeder>();
// Expose the subscriber's live health to /healthz.
builder.Services.AddSingleton(sp =>
    (SecurityBusSubscriber)sp.GetServices<IHostedService>()
        .First(s => s is SecurityBusSubscriber));

var app = builder.Build();

app.UseDefaultFiles();     // serve wwwroot/index.html at "/"
app.UseStaticFiles();

// ── inspection API (read-only) ────────────────────────────────────────────────
app.MapGet("/api/posture", (ReadModel rm) => Results.Json(rm.Posture()));
app.MapGet("/api/recent", (ReadModel rm, int? n) => Results.Json(rm.Recent(Math.Clamp(n ?? 200, 1, 2000))));
app.MapGet("/api/chain/{id}", (ReadModel rm, string id) => Results.Json(rm.Chain(id)));
app.MapGet("/api/stats", (ReadModel rm, LiveFeed feed, SecurityBusSubscriber sub) =>
    Results.Json(new { total = rm.Total, ingested = sub.Ingested, connected = sub.Connected, clients = feed.ClientCount }));

app.MapGet("/healthz", (SecurityBusSubscriber sub) =>
    sub.Connected ? Results.Ok("ok") : Results.Json(new { status = "degraded", reason = "bus not connected" }, statusCode: 503));

// ── live feed (Server-Sent Events) ────────────────────────────────────────────
app.MapGet("/events", async (HttpContext ctx, LiveFeed feed, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    using var sub = feed.Subscribe();
    await ctx.Response.WriteAsync(": connected\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);
    try
    {
        await foreach (var d in sub.ReadAllAsync(ct))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(d,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
});

var url = $"http://0.0.0.0:{EnvInt("SENTINEL_CONSOLE_PORT", 8140)}";
app.Run(url);

static int EnvInt(string k, int dflt) =>
    int.TryParse(Environment.GetEnvironmentVariable(k), out var v) && v > 0 ? v : dflt;
