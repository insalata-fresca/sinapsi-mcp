using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Host;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cervello.Enrichment.Host.Tests;

/// <summary>
/// The S50 context-pack + map-read + capture HTTP surfaces (<see cref="ContextPackApi"/> +
/// <see cref="CaptureApi"/>) — the token-gated transport the CT145 bridge fronts. Proves the TRANSPORT
/// contract end-to-end over a real in-process server (the assembler + services are proven in the lib
/// tests): bearer gating (401, fail-closed), the exact §2.1 pack wire shape, and confirm-by-default.
/// All against fakes — NO DB, NO network, NO personal data.
/// </summary>
public sealed class ContextPackApiTests
{
    private const string Token = "cervello-pack-token";

    // ── a real in-process host mapping the routes over fake-wired services ───────────────────────
    private static TestServer Server(string gateToken, out FakeGraph graph, out InMemoryDepositStore deposits)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var g = new FakeGraph();
        var idx = new FakeIndex();
        var openPoints = new InMemoryOpenPointStore();
        var cursor = new InMemoryDeltaCursorStore();
        var dep = new InMemoryDepositStore();
        var writer = new CervelloGraphWriter(new HostFakeMapPrWriter(), new HostFakeLinkResolver(), new HostFakePinStore());

        builder.Services.AddSingleton<IOpenPointsAuthGate>(new TokenOpenPointsAuthGate(gateToken));
        builder.Services.AddSingleton<IMapGraph>(g);
        builder.Services.AddSingleton(new ContextPackAssembler(idx, g, openPoints, cursor));
        builder.Services.AddSingleton(new CaptureService(dep));
        builder.Services.AddSingleton(new GoalService(g, writer));

        var app = builder.Build();
        app.MapContextPack(30_000);
        app.MapCapture();
        app.Start();
        graph = g;
        deposits = dep;
        return app.GetTestServer();
    }

    private static HttpRequestMessage Post(string path, object body, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (bearer is not null) req.Headers.Add("Authorization", $"Bearer {bearer}");
        return req;
    }

    private static HttpRequestMessage Get(string path, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null) req.Headers.Add("Authorization", $"Bearer {bearer}");
        return req;
    }

    // ── auth (fail-closed) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Context_pack_without_bearer_is_401()
    {
        using var srv = Server(Token, out _, out _);
        var resp = await srv.CreateClient().SendAsync(Post("/context-pack", new { intent = "portfolio" }, bearer: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Empty_gate_token_fails_closed_on_every_route()
    {
        using var srv = Server(gateToken: "", out _, out _);
        var client = srv.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/object?kind=goal&id=x", bearer: "anything"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post("/capture", new { fact = "x", confirm = true }, bearer: "anything"))).StatusCode);
    }

    // ── context-pack wire shape (§2.1) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Context_pack_returns_the_design_wire_shape()
    {
        using var srv = Server(Token, out var graph, out _);
        graph.Objects["Goal:g"] = Goal("g");
        graph.Goals.Add("g");

        var resp = await srv.CreateClient().SendAsync(Post("/context-pack", new { intent = "goal_reasoning", focus = "g", budget = 5000 }, Token));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("goal_reasoning", root.GetProperty("intent").GetString());
        Assert.Equal("g", root.GetProperty("focus").GetString());
        Assert.Equal(5000, root.GetProperty("budget").GetInt32());
        Assert.True(root.TryGetProperty("used", out _));
        Assert.True(root.TryGetProperty("as_of", out _));
        Assert.True(root.TryGetProperty("sections", out _));
        // coverage is mandatory (§2.1): the three arrays are always present.
        var cov = root.GetProperty("coverage");
        Assert.True(cov.TryGetProperty("looked_at", out _));
        Assert.True(cov.TryGetProperty("deferred", out _));
        Assert.True(cov.TryGetProperty("gaps", out _));
        Assert.True(root.TryGetProperty("open_points", out _));
        // delta is present for goal_reasoning (§2.6).
        Assert.True(root.TryGetProperty("delta", out _));
    }

    [Fact]
    public async Task Context_pack_rejects_an_unknown_intent()
    {
        using var srv = Server(Token, out _, out _);
        var resp = await srv.CreateClient().SendAsync(Post("/context-pack", new { intent = "nonsense" }, Token));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── /object + /timeline ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Object_returns_the_dossier_verbatim_or_404()
    {
        using var srv = Server(Token, out var graph, out _);
        graph.Objects["Goal:g"] = Goal("g");
        var client = srv.CreateClient();

        var ok = await client.SendAsync(Get("/object?kind=goal&id=g", Token));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal("goal", doc.RootElement.GetProperty("kind").GetString());
        Assert.True(doc.RootElement.TryGetProperty("body_markdown", out _));
        Assert.True(doc.RootElement.TryGetProperty("sources", out _));

        var missing = await client.SendAsync(Get("/object?kind=goal&id=ghost", Token));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Timeline_returns_sourced_lines()
    {
        using var srv = Server(Token, out var graph, out _);
        graph.Timelines["goal:g"] = new() { new("2026-06-01", "moved", new[] { "g" }, "rec://a") };

        var resp = await srv.CreateClient().SendAsync(Get("/timeline?anchor=goal:g", Token));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var line = doc.RootElement.GetProperty("lines")[0];
        Assert.Equal("2026-06-01", line.GetProperty("date").GetString());
        Assert.Equal("rec://a", line.GetProperty("source").GetString());
    }

    // ── capture confirm-by-default (§5.5) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Capture_preview_writes_nothing()
    {
        using var srv = Server(Token, out _, out var deposits);
        var resp = await srv.CreateClient().SendAsync(Post("/capture", new { fact = "a fact", confirm = false }, Token));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("preview", doc.RootElement.GetProperty("status").GetString());
        var id = doc.RootElement.GetProperty("deposit_id").GetString()!;
        Assert.False(deposits.Exists(id));
    }

    [Fact]
    public async Task Capture_confirm_deposits()
    {
        using var srv = Server(Token, out _, out var deposits);
        var resp = await srv.CreateClient().SendAsync(Post("/capture", new { fact = "a fact", confirm = true }, Token));
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("deposited", doc.RootElement.GetProperty("status").GetString());
        Assert.StartsWith("human://", doc.RootElement.GetProperty("basis").GetString());
    }

    // ── set_goal + evidence ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Set_goal_preview_then_confirm()
    {
        using var srv = Server(Token, out _, out _);
        var client = srv.CreateClient();

        var preview = await client.SendAsync(Post("/goal", new { name = "New Goal", confirm = false }, Token));
        var pd = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal("preview", pd.RootElement.GetProperty("status").GetString());
        Assert.Contains("type: goal", pd.RootElement.GetProperty("preview").GetString());

        var confirm = await client.SendAsync(Post("/goal", new { name = "New Goal", confirm = true }, Token));
        var cd = JsonDocument.Parse(await confirm.Content.ReadAsStringAsync());
        Assert.Equal("created", cd.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Evidence_on_unknown_goal_is_404()
    {
        using var srv = Server(Token, out _, out _);
        var resp = await srv.CreateClient().SendAsync(
            Post("/goal/ghost/evidence", new { evidence_ref = "rec://x", fact = "f", confirm = true }, Token));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── fakes ─────────────────────────────────────────────────────────────────────────────────

    private static MapObject Goal(string slug) => new(
        MapObjectKind.Goal, slug,
        new Dictionary<string, string> { ["type"] = "goal", ["name"] = slug, ["status"] = "active" },
        $"# {slug}\n\n## Stato\non track\n", new[] { $"map/goals/{slug}.md" });

    private sealed class FakeGraph : IMapGraph
    {
        public Dictionary<string, MapObject> Objects { get; } = new();
        public Dictionary<string, List<TimelineLine>> Timelines { get; } = new();
        public List<string> Goals { get; } = new();
        public Task<MapObject?> GetObjectAsync(MapObjectKind kind, string slug, CancellationToken ct = default) =>
            Task.FromResult(Objects.TryGetValue($"{kind}:{slug}", out var o) ? o : null);
        public Task<IReadOnlyList<TimelineLine>> WalkTimelineAsync(string anchor, string? from, string? to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TimelineLine>>(Timelines.TryGetValue(anchor, out var l) ? l : new List<TimelineLine>());
        public Task<IReadOnlyList<GraphNeighbour>> NeighboursAsync(MapObjectKind kind, string slug, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GraphNeighbour>>(Array.Empty<GraphNeighbour>());
        public Task<IReadOnlyList<string>> ListGoalSlugsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Goals);
    }

    private sealed class FakeIndex : IIndexerSearch
    {
        public Task<IReadOnlyList<IndexerHit>> SearchAsync(string query, string? kind, int? limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IndexerHit>>(Array.Empty<IndexerHit>());
    }
}
