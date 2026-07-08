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
/// CB-BRIDGE §5 — the cervello dialogue DEPOSIT tools (capture_fact, set_goal, link_evidence).
/// Mirrors OpenPointsToolsTests. Proves the write-back contract at the bridge edge:
///   - CERVELLO_EXPOSED=false → disabled BEFORE any I/O.
///   - empty effective pack token → not_configured BEFORE any I/O.
///   - transport failure → unreachable; CT146 401 → unauthorized.
///   - confirm=false forwards confirm:false (preview); confirm=true forwards confirm:true.
///   - the right route / body / bearer is sent upstream; response passed through verbatim.
///   - deposit scope gate: a read-only (or shared) bearer is REFUSED (isolation).
/// All against a mock handler — no CT146, no personal data.
/// </summary>
public sealed class CervelloDepositToolsTests
{
    [Fact]
    public async Task CaptureFact_Disabled_ReturnsDisabled_BeforeAnyIo()
    {
        var (tool, handler) = Build(packToken: "tok", exposed: false);
        var json = ToJson(await tool.CaptureFact("fact"));
        Assert.Equal("disabled", json.GetProperty("status").GetString());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SetGoal_EmptyToken_ReturnsNotConfigured_BeforeAnyIo()
    {
        var (tool, handler) = Build(packToken: "", openPointsToken: "");
        var json = ToJson(await tool.SetGoal("Ship ETRM"));
        Assert.Equal("not_configured", json.GetProperty("status").GetString());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task LinkEvidence_TransportFailure_ReturnsUnreachable()
    {
        var (tool, _) = Build(packToken: "tok",
            handlerFunc: _ => throw new HttpRequestException("refused"));
        var json = ToJson(await tool.LinkEvidence("etrm", "rec://r1", "made progress"));
        Assert.Equal("unreachable", json.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(400, "bad_request")]
    [InlineData(401, "unauthorized")]
    [InlineData(404, "not_found")]
    [InlineData(500, "error")]
    public async Task CaptureFact_NonSuccessStatus_ReturnsEnvelope(int httpStatus, string expected)
    {
        var (tool, _) = Build(packToken: "tok",
            handlerFunc: _ => new HttpResponseMessage((HttpStatusCode)httpStatus)
            {
                Content = new StringContent("{\"error\":\"x\"}", Encoding.UTF8, "application/json"),
            });
        var json = ToJson(await tool.CaptureFact("fact"));
        Assert.Equal(expected, json.GetProperty("status").GetString());
        Assert.Equal(httpStatus, json.GetProperty("http_status").GetInt32());
    }

    // ── confirm-by-default semantics forwarded verbatim ─────────────────────────────────────────
    [Fact]
    public async Task CaptureFact_ConfirmFalse_ForwardsPreview()
    {
        string? body = null;
        var (tool, _) = Build(packToken: "tok",
            handlerFunc: req => { body = req.Content?.ReadAsStringAsync().Result; return Ok("{\"status\":\"preview\"}"); });
        await tool.CaptureFact("Guilhem joined the ETRM call", sourceHint: "said 2026-07-08",
            relatesTo: new[] { "goal:etrm" }, confirm: false);
        Assert.Contains("\"confirm\":false", body);
        Assert.Contains("\"fact\":\"Guilhem joined the ETRM call\"", body);
        Assert.Contains("\"source_hint\":\"said 2026-07-08\"", body);
        Assert.Contains("\"relates_to\":[\"goal:etrm\"]", body);
    }

    [Fact]
    public async Task CaptureFact_PostsToCaptureRoute_WithBearer()
    {
        HttpRequestMessage? captured = null;
        var (tool, _) = Build(packToken: "cap-tok",
            handlerFunc: req => { captured = req; return Ok("{\"status\":\"deposited\",\"deposit_id\":\"d1\"}"); });
        var json = ToJson(await tool.CaptureFact("fact", confirm: true));
        Assert.Equal("deposited", json.GetProperty("status").GetString());
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("/capture", captured.RequestUri!.ToString());
        Assert.Equal("Bearer cap-tok", captured.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SetGoal_PostsToGoalRoute_WithFields()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var (tool, _) = Build(packToken: "tok",
            handlerFunc: req => { captured = req; body = req.Content?.ReadAsStringAsync().Result; return Ok("{\"status\":\"pr_opened\",\"goal_slug\":\"ship-etrm\"}"); });

        var json = ToJson(await tool.SetGoal("Ship ETRM", status: "active", objective: "GA by Q3",
            people: new[] { "guilhem" }, nextSteps: new[] { "cut release" }, confirm: true));

        Assert.Equal("ship-etrm", json.GetProperty("goal_slug").GetString());
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("/goal", captured.RequestUri!.ToString());
        Assert.Contains("\"name\":\"Ship ETRM\"", body);
        Assert.Contains("\"status\":\"active\"", body);
        Assert.Contains("\"next_steps\":[\"cut release\"]", body);
        Assert.Contains("\"confirm\":true", body);
    }

    [Fact]
    public async Task LinkEvidence_PostsToEvidenceRoute_WithSlugInPath()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var (tool, _) = Build(packToken: "tok",
            handlerFunc: req => { captured = req; body = req.Content?.ReadAsStringAsync().Result; return Ok("{\"status\":\"pr_opened\",\"line\":\"- 2026-07-08 — x — source: rec://r1\"}"); });

        await tool.LinkEvidence("ship-etrm", "rec://r1", "demo landed", date: "2026-07-08", confirm: true);

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("/goal/ship-etrm/evidence", captured.RequestUri!.ToString());
        Assert.Contains("\"evidence_ref\":\"rec://r1\"", body);
        Assert.Contains("\"fact\":\"demo landed\"", body);
        Assert.Contains("\"date\":\"2026-07-08\"", body);
        Assert.Contains("\"confirm\":true", body);
    }

    // ── isolation: a bearer WITHOUT the deposit scope is refused ─────────────────────────────────
    [Fact]
    public async Task ReadOnlyBearer_CannotReach_DepositTools()
    {
        var (tool, handler) = Build(packToken: "tok");
        // Cervello READ scope only — no deposit scope.
        BridgeAuthState.CurrentAuth = new BridgeAuthContext
        {
            Mode = "jwt", Subject = "cervello-read-only",
            Scopes = new HashSet<string>(StringComparer.Ordinal) { AuthService.CervelloReadScope },
            RawToken = "read-only-jwt",
        };
        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tool.CaptureFact("fact"));
        Assert.Contains("insufficient_scope", ex.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SharedLegacyBearer_CannotReach_DepositTools()
    {
        var (tool, handler) = Build(packToken: "tok");
        BridgeAuthState.CurrentAuth = new BridgeAuthContext
        {
            Mode = "bearer", Subject = "legacy-bearer", Scopes = LegacyScopes.All, RawToken = "shared-bearer",
        };
        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tool.SetGoal("x"));
        Assert.Contains("insufficient_scope", ex.Message);
        Assert.Equal(0, handler.CallCount);
    }

    // ── structural ────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("CaptureFact", "cervello_capture_fact")]
    [InlineData("SetGoal", "cervello_set_goal")]
    [InlineData("LinkEvidence", "cervello_link_evidence")]
    public void ToolMethods_CarryCorrectMcpNames(string method, string toolName)
    {
        var m = typeof(BridgeCervelloDepositTools).GetMethod(method,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(m);
        var attr = m!.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
            .Cast<ModelContextProtocol.Server.McpServerToolAttribute>().FirstOrDefault();
        Assert.NotNull(attr);
        Assert.Equal(toolName, attr!.Name);
    }

    [Fact]
    public void ToolType_IsDecorated()
        => Assert.Single(typeof(BridgeCervelloDepositTools).GetCustomAttributes(
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

    private static (BridgeCervelloDepositTools tool, MockHttpMessageHandler handler) Build(
        string packToken,
        string? openPointsToken = "op",
        bool exposed = true,
        Func<HttpRequestMessage, HttpResponseMessage>? handlerFunc = null)
    {
        var handler = new MockHttpMessageHandler(handlerFunc);
        var services = new ServiceCollection();
        services.AddHttpClient("cervello-capture").ConfigurePrimaryHttpMessageHandler(() => handler);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        var cfg = new BridgeConfig
        {
            CervelloPackToken = packToken,
            CervelloOpenPointsToken = openPointsToken ?? "",
            CervelloCaptureUrl = "http://localhost:8147",
            CervelloExposed = exposed,
        };

        BridgeAuthState.CurrentAuth = CervelloScopedAuth();
        var auth = new AuthService(cfg, new BridgeRateLimiter());
        var audit = new AuditService(cfg, NullLogger<AuditService>.Instance);
        return (new BridgeCervelloDepositTools(auth, audit, cfg, factory), handler);
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
