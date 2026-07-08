using System.Net;
using System.Text;
using System.Text.Json;
using Bridge.Mcp;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Bridge.Mcp.Audit;
using Xunit;

namespace Bridge.Mcp.Tests;

/// <summary>
/// CB-BRIDGE §5 — the cervello dialogue READ tools (context_pack, search, get, timeline_walk).
/// Mirrors OpenPointsToolsTests. Proves the exposure contract at the bridge edge for each tool:
///   - CERVELLO_EXPOSED=false → {status:"disabled"} BEFORE any I/O.
///   - empty effective token → {status:"not_configured"} BEFORE any I/O.
///   - transport failure → {status:"unreachable"}.
///   - CT146 401 → {status:"unauthorized", http_status:401}.
///   - success → the CT146 body passed through verbatim.
///   - the right method / route / bearer / body is sent upstream.
///   - the shared legacy bearer (no bridge:cervello:*) is REFUSED (isolation).
/// All against a mock handler — no CT146, no personal data.
/// </summary>
public sealed class CervelloReadToolsTests
{
    // ── disabled / not_configured (all four tools) ──────────────────────────────────────────────
    [Fact]
    public async Task ContextPack_Disabled_ReturnsDisabled_BeforeAnyIo()
    {
        var (tool, handler) = Build(packToken: "tok", exposed: false);
        var json = ToJson(await tool.ContextPack("recall", "foo"));
        Assert.Equal("disabled", json.GetProperty("status").GetString());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ContextPack_EmptyToken_ReturnsNotConfigured_BeforeAnyIo()
    {
        // No pack token AND no open-points fallback → effective empty.
        var (tool, handler) = Build(packToken: "", openPointsToken: "");
        var json = ToJson(await tool.ContextPack("recall", "foo"));
        Assert.Equal("not_configured", json.GetProperty("status").GetString());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Search_EmptyToken_ReturnsNotConfigured_BeforeAnyIo()
    {
        var (tool, handler) = Build(packToken: "tok", searchToken: "");
        var json = ToJson(await tool.Search("query"));
        Assert.Equal("not_configured", json.GetProperty("status").GetString());
        Assert.Equal(0, handler.CallCount);
    }

    // ── token fallback: pack token empty but open-points present → I/O proceeds ──────────────────
    [Fact]
    public async Task ContextPack_FallsBackToOpenPointsToken()
    {
        HttpRequestMessage? captured = null;
        var (tool, _) = Build(packToken: "", openPointsToken: "op-fallback",
            handlerFunc: req =>
            {
                captured = req;
                return Ok("{\"intent\":\"recall\",\"used\":10,\"sections\":[]}");
            });
        var json = ToJson(await tool.ContextPack("recall", "foo"));
        Assert.Equal("recall", json.GetProperty("intent").GetString());
        Assert.Equal("Bearer op-fallback", captured!.Headers.Authorization?.ToString());
    }

    // ── transport + non-ok ──────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ContextPack_TransportFailure_ReturnsUnreachable()
    {
        var (tool, _) = Build(packToken: "tok",
            handlerFunc: _ => throw new HttpRequestException("Connection refused"));
        var json = ToJson(await tool.ContextPack("recall", "foo"));
        Assert.Equal("unreachable", json.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(400, "bad_request")]
    [InlineData(401, "unauthorized")]
    [InlineData(404, "not_found")]
    [InlineData(500, "error")]
    public async Task Search_NonSuccessStatus_ReturnsEnvelope(int httpStatus, string expected)
    {
        var (tool, _) = Build(packToken: "tok", searchToken: "stok",
            handlerFunc: _ => new HttpResponseMessage((HttpStatusCode)httpStatus)
            {
                Content = new StringContent("{\"error\":\"x\"}", Encoding.UTF8, "application/json"),
            });
        var json = ToJson(await tool.Search("q"));
        Assert.Equal(expected, json.GetProperty("status").GetString());
        Assert.Equal(httpStatus, json.GetProperty("http_status").GetInt32());
    }

    // ── upstream shape: method / route / body / bearer ───────────────────────────────────────────
    [Fact]
    public async Task ContextPack_PostsIntentFocusBudgetSince_WithBearer()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var (tool, _) = Build(packToken: "pack-tok",
            handlerFunc: req =>
            {
                captured = req;
                body = req.Content?.ReadAsStringAsync().Result;
                return Ok("{\"intent\":\"goal_reasoning\",\"used\":0,\"sections\":[]}");
            });

        await tool.ContextPack("goal_reasoning", "goal:etrm", budget: 12000, since: "2026-07-01T00:00:00Z");

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("/context-pack", captured.RequestUri!.ToString());
        Assert.Equal("Bearer pack-tok", captured.Headers.Authorization?.ToString());
        Assert.Contains("\"intent\":\"goal_reasoning\"", body);
        Assert.Contains("\"focus\":\"goal:etrm\"", body);
        Assert.Contains("\"budget\":12000", body);
        Assert.Contains("\"since\":\"2026-07-01T00:00:00Z\"", body);
    }

    [Fact]
    public async Task ContextPack_DefaultsBudgetFromConfig_WhenOmitted()
    {
        string? body = null;
        var (tool, _) = Build(packToken: "tok", packBudget: 42000,
            handlerFunc: req => { body = req.Content?.ReadAsStringAsync().Result; return Ok("{\"used\":0}"); });
        await tool.ContextPack("recall", "foo");
        Assert.Contains("\"budget\":42000", body);
    }

    [Fact]
    public async Task Search_SendsGetWithQueryKindLimit_AndSearchBearer()
    {
        HttpRequestMessage? captured = null;
        var (tool, _) = Build(packToken: "pack", searchToken: "search-tok",
            handlerFunc: req => { captured = req; return Ok("{\"query\":\"x\",\"count\":0,\"hits\":[]}"); });

        await tool.Search("etrm mining", kind: "goal", limit: 5);

        // RequestUri.ToString() renders the DECODED query for display; the wire form is escaped
        // (Uri.EscapeDataString) but AbsoluteUri decodes back for readability. Assert on both:
        // the space survives round-trip, and kind/limit are present.
        var uri = captured!.RequestUri!.ToString();
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Contains("/search", uri);
        Assert.Contains("q=etrm mining", uri);
        Assert.Contains("kind=goal", uri);
        Assert.Contains("limit=5", uri);
        Assert.Equal("Bearer search-tok", captured.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Get_SendsGetToObjectWithKindAndId()
    {
        HttpRequestMessage? captured = null;
        var (tool, _) = Build(packToken: "tok",
            handlerFunc: req => { captured = req; return Ok("{\"kind\":\"goal\",\"id\":\"etrm\"}"); });
        await tool.Get("goal", "etrm");
        var uri = captured!.RequestUri!.ToString();
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Contains("/object", uri);
        Assert.Contains("kind=goal", uri);
        Assert.Contains("id=etrm", uri);
    }

    [Fact]
    public async Task TimelineWalk_SendsGetToTimelineWithAnchorRange()
    {
        HttpRequestMessage? captured = null;
        var (tool, _) = Build(packToken: "tok",
            handlerFunc: req => { captured = req; return Ok("{\"anchor\":\"goal:etrm\",\"lines\":[]}"); });
        await tool.TimelineWalk("goal:etrm", from: "2026-06-01", to: "2026-07-01");
        var uri = captured!.RequestUri!.ToString();
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Contains("/timeline", uri);
        Assert.Contains("anchor=goal%3Aetrm", uri); // ':' stays percent-encoded in the query
        Assert.Contains("from=2026-06-01", uri);
        Assert.Contains("to=2026-07-01", uri);
    }

    [Fact]
    public async Task Get_Success_PassesThroughBodyVerbatim()
    {
        var (tool, _) = Build(packToken: "tok",
            handlerFunc: _ => Ok("{\"kind\":\"goal\",\"id\":\"etrm\",\"sources\":[\"rec://r1\"]}"));
        var json = ToJson(await tool.Get("goal", "etrm"));
        Assert.Equal("etrm", json.GetProperty("id").GetString());
        Assert.Equal("rec://r1", json.GetProperty("sources")[0].GetString());
    }

    // ── isolation: shared legacy bearer must NOT reach cervello ───────────────────────────────────
    [Fact]
    public async Task SharedLegacyBearer_CannotReach_ReadTools()
    {
        var (tool, handler) = Build(packToken: "tok");
        BridgeAuthState.CurrentAuth = new BridgeAuthContext
        {
            Mode = "bearer", Subject = "legacy-bearer", Scopes = LegacyScopes.All, RawToken = "shared-bearer",
        };
        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tool.ContextPack("recall", "foo"));
        Assert.Contains("insufficient_scope", ex.Message);
        Assert.Equal(0, handler.CallCount);
    }

    // ── structural: MCP names + decoration ────────────────────────────────────────────────────────
    [Theory]
    [InlineData("ContextPack", "cervello_context_pack")]
    [InlineData("Search", "cervello_search")]
    [InlineData("Get", "cervello_get")]
    [InlineData("TimelineWalk", "cervello_timeline_walk")]
    public void ToolMethods_CarryCorrectMcpNames(string method, string toolName)
    {
        var m = typeof(BridgeCervelloReadTools).GetMethod(method,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(m);
        var attr = m!.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
            .Cast<ModelContextProtocol.Server.McpServerToolAttribute>().FirstOrDefault();
        Assert.NotNull(attr);
        Assert.Equal(toolName, attr!.Name);
    }

    [Fact]
    public void ToolType_IsDecorated()
        => Assert.Single(typeof(BridgeCervelloReadTools).GetCustomAttributes(
            typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), false));

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────
    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? func = null)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(func?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (BridgeCervelloReadTools tool, MockHttpMessageHandler handler) Build(
        string packToken,
        string? openPointsToken = "op",
        string? searchToken = "stok",
        bool exposed = true,
        int packBudget = 30000,
        Func<HttpRequestMessage, HttpResponseMessage>? handlerFunc = null)
    {
        var handler = new MockHttpMessageHandler(handlerFunc);
        var services = new ServiceCollection();
        // All three named clients share the mock handler in the test.
        services.AddHttpClient("cervello-pack").ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient("cervello-search").ConfigurePrimaryHttpMessageHandler(() => handler);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        var cfg = new BridgeConfig
        {
            CervelloPackToken = packToken,
            CervelloOpenPointsToken = openPointsToken ?? "",
            CervelloSearchToken = searchToken ?? "",
            CervelloPackUrl = "http://localhost:8147",
            CervelloSearchUrl = "http://localhost:8009",
            CervelloExposed = exposed,
            CervelloPackBudgetDefault = packBudget,
        };

        BridgeAuthState.CurrentAuth = CervelloScopedAuth();
        var auth = new AuthService(cfg, new BridgeRateLimiter());
        var audit = new AuditService(cfg, NullLogger<AuditService>.Instance);
        return (new BridgeCervelloReadTools(auth, audit, cfg, factory), handler);
    }

    private static BridgeAuthContext CervelloScopedAuth() => new()
    {
        Mode = "jwt", Subject = "cervello-project",
        Scopes = new HashSet<string>(StringComparer.Ordinal)
        {
            AuthService.CervelloReadScope, AuthService.CervelloDepositScope,
        },
        RawToken = "cervello-scoped-jwt",
    };

    private static JsonElement ToJson(object result)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement;
    }
}
