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
/// The S50 L3 open-points HTTP surface (<see cref="OpenPointsApi"/>) — the token-gated transport the
/// CT145 bridge fronts so the operator's claude.ai app can list + answer open-points. Proves the
/// TRANSPORT contract end-to-end over a real in-process server (the engine <c>OpenPointsService</c>
/// logic itself is proven separately in Cervello.Enrichment.Tests):
///
/// <list type="bullet">
/// <item>GET/POST WITHOUT a bearer → 401 (SearchAuth lesson — never an unauthenticated read/write).</item>
/// <item>GET WITH the wrong bearer → 401.</item>
/// <item>GET WITH the right bearer → 200 + the redacted list shape (refs + question + candidates).</item>
/// <item>POST answer (select) WITH the right bearer → 200 + applied + human:// basis.</item>
/// <item>POST answer for an unknown point → 404.</item>
/// <item>a mis-provisioned gate (empty token) → 401 on every call (fail-closed).</item>
/// </list>
/// All against fakes — no DB, no network, NO personal data.
/// </summary>
public sealed class OpenPointsApiTests
{
    private const string Token = "l3-open-points-token";
    private const string Rec = "rec-2026-07-01-standup";
    private const string Bundle = "2026-07-01-standup";

    // ── build a real in-process host mapping the open-points routes over a fake-wired service ──
    private static TestServer Server(string gateToken, out InMemoryOpenPointStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var s = new InMemoryOpenPointStore();
        var writer = new CervelloGraphWriter(new FakeMapPrWriter(), new FakeLinkResolver(), new FakePinStore());
        var allowlist = EnrollmentAllowlist.Empty;
        var voiceprints = new InMemoryVoiceprintStore(allowlist);
        var svc = new OpenPointsService(
            new TokenOpenPointsAuthGate(gateToken),
            s,
            new FakeAccessLog(),
            writer,
            new InMemoryCorrectionMapStore(),
            new VoiceprintEnrollment(voiceprints),
            allowlist,
            new FakeEnrollmentSourceProvider());

        builder.Services.AddSingleton(svc);
        var app = builder.Build();
        app.MapOpenPoints();
        app.Start();
        store = s;
        return app.GetTestServer();
    }

    private static HttpRequestMessage Get(string path, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null) req.Headers.Add("Authorization", $"Bearer {bearer}");
        return req;
    }

    private static OpenPoint SpeakerPoint(string id) =>
        new(id, OpenPointKind.Speaker, Rec, Bundle, "which enrolled person is s1?",
            new[] { new ScoredCandidate("guilhem", 0.55, "voice 0.55; filename prior") }, mergedSpeaker: "s1");

    [Fact]
    public async Task List_without_bearer_is_401()
    {
        using var srv = Server(Token, out _);
        var client = srv.CreateClient();
        var resp = await client.SendAsync(Get("/open-points", bearer: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task List_with_wrong_bearer_is_401()
    {
        using var srv = Server(Token, out _);
        var client = srv.CreateClient();
        var resp = await client.SendAsync(Get("/open-points", bearer: "not-the-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task List_with_right_bearer_returns_redacted_points()
    {
        using var srv = Server(Token, out var store);
        await store.EnqueueAsync(SpeakerPoint("op_1"));
        var client = srv.CreateClient();

        var resp = await client.SendAsync(Get("/open-points", bearer: Token));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        var p = doc.RootElement.GetProperty("open_points")[0];
        Assert.Equal("op_1", p.GetProperty("point_id").GetString());
        Assert.Equal("speaker", p.GetProperty("kind").GetString());
        Assert.Equal($"rec://{Rec}", p.GetProperty("recording").GetString());
        // Redaction: only refs + question + scored candidates, no transcript/audio field.
        var cand = p.GetProperty("candidates")[0];
        Assert.Equal("guilhem", cand.GetProperty("value").GetString());
        Assert.False(p.TryGetProperty("transcript", out _));
    }

    [Fact]
    public async Task Answer_select_applies_with_human_basis()
    {
        using var srv = Server(Token, out var store);
        await store.EnqueueAsync(SpeakerPoint("op_1"));
        var client = srv.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/open-points/op_1/answer")
        {
            Content = JsonContent.Create(new { mode = "select", value = "guilhem" }),
        };
        req.Headers.Add("Authorization", $"Bearer {Token}");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("applied", doc.RootElement.GetProperty("status").GetString());
        var basis = doc.RootElement.GetProperty("basis").GetString();
        Assert.StartsWith("human://", basis);
        // Idempotent: the point is now resolved.
        Assert.True(await store.IsResolvedAsync("op_1"));
    }

    [Fact]
    public async Task Answer_unknown_point_is_404()
    {
        using var srv = Server(Token, out _);
        var client = srv.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/open-points/op_missing/answer")
        {
            Content = JsonContent.Create(new { mode = "dismiss" }),
        };
        req.Headers.Add("Authorization", $"Bearer {Token}");
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Empty_gate_token_fails_closed_401()
    {
        using var srv = Server(gateToken: "", out var store);
        await store.EnqueueAsync(SpeakerPoint("op_1"));
        var client = srv.CreateClient();
        // even presenting a token: the gate is unconfigured → refuses everything.
        var resp = await client.SendAsync(Get("/open-points", bearer: "anything"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── minimal inline fakes (self-contained; the engine's own fakes are internal to its test asm) ──

    private sealed class FakeAccessLog : IAccessLog
    {
        public Task AppendAsync(AccessLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeMapPrWriter : IMapPrWriter
    {
        public Task<MapPrHandle> OpenPrAsync(MapReviewPr pr, CancellationToken ct = default) =>
            Task.FromResult(new MapPrHandle("cervello/op-op_1", "test-pr"));
    }

    private sealed class FakeLinkResolver : ILinkResolver
    {
        public Task<bool> DossierExistsAsync(string slug, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakePinStore : IPinStore
    {
        public Task<string> PinAsync(string externalRef, CancellationToken ct = default) =>
            Task.FromResult("pin://deadbeef");
    }

    private sealed class FakeEnrollmentSourceProvider : IEnrollmentSourceProvider
    {
        public Task<EnrollmentSource?> GetConfirmedSourceAsync(string recordingId, string mergedSpeaker, CancellationToken ct = default) =>
            Task.FromResult<EnrollmentSource?>(null); // no centroid → no enroll (person not on the empty allowlist anyway)
    }
}
