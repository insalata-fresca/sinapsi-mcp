using System.Net;
using System.Text;
using System.Text.Json;
using Bridge.Mcp;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Mcp.Tests;

/// <summary>
/// S50 L3 — the cervello open-points Surface A bridge tools (cervello_open_points_list /
/// cervello_open_points_answer). Mirrors the CareerSearchToolsTests harness. Proves the exposure
/// contract at the bridge edge:
///   - CERVELLO_EXPOSED=false → {status:"disabled"} BEFORE any I/O (ACCESS.md §7 emergency-disable).
///   - empty CERVELLO_OPEN_POINTS_TOKEN → {status:"not_configured"} BEFORE any I/O.
///   - transport failure → {status:"unreachable"}.
///   - CT146 401 → {status:"unauthorized", http_status:401} (fail-closed passthrough).
///   - success list → the redacted CT146 body passed through.
///   - answer POSTs {mode,value} to /open-points/{id}/answer with the cervello bearer.
///   - both tools carry the correct MCP names + [McpServerToolType].
/// All against a mock handler — no CT146, no personal data.
/// </summary>
public sealed class OpenPointsToolsTests
{
    [Fact]
    public async Task Disabled_ReturnsDisabled_BeforeAnyIo()
    {
        var (tool, handler) = BuildTool(token: "tok", exposed: false);
        var result = await tool.ListOpenPoints();
        var json = ToJsonElement(result);
        Assert.Equal("disabled", json.GetProperty("status").GetString());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Answer_Disabled_ReturnsDisabled_BeforeAnyIo()
    {
        var (tool, handler) = BuildTool(token: "tok", exposed: false);
        var result = await tool.AnswerOpenPoint("op_1", "select", "guilhem");
        var json = ToJsonElement(result);
        Assert.Equal("disabled", json.GetProperty("status").GetString());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task EmptyToken_ReturnsNotConfigured_BeforeAnyIo()
    {
        var (tool, handler) = BuildTool(token: "");
        var result = await tool.ListOpenPoints();
        var json = ToJsonElement(result);
        Assert.Equal("not_configured", json.GetProperty("status").GetString());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TransportFailure_ReturnsUnreachable()
    {
        var (tool, _) = BuildTool(token: "tok",
            handlerFunc: _ => throw new HttpRequestException("Connection refused"));
        var result = await tool.ListOpenPoints();
        var json = ToJsonElement(result);
        Assert.Equal("unreachable", json.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(400, "bad_request")]
    [InlineData(401, "unauthorized")]
    [InlineData(404, "not_found")]
    [InlineData(500, "error")]
    public async Task NonSuccessStatus_ReturnsEnvelope(int httpStatus, string expected)
    {
        var (tool, _) = BuildTool(token: "tok",
            handlerFunc: _ => new HttpResponseMessage((HttpStatusCode)httpStatus)
            {
                Content = new StringContent("{\"error\":\"x\"}", Encoding.UTF8, "application/json"),
            });
        var result = await tool.ListOpenPoints();
        var json = ToJsonElement(result);
        Assert.Equal(expected, json.GetProperty("status").GetString());
        Assert.Equal(httpStatus, json.GetProperty("http_status").GetInt32());
    }

    [Fact]
    public async Task List_Success_PassesThroughRedactedBody()
    {
        var ct146Body = new
        {
            count = 1,
            open_points = new[]
            {
                new
                {
                    point_id = "op_1", kind = "speaker", recording = "rec://r1", bundle = "bundle://b1",
                    question = "which enrolled person is s1?",
                    candidates = new[] { new { value = "guilhem", confidence = 0.55, why = "voice 0.55" } },
                },
            },
        };
        var (tool, _) = BuildTool(token: "tok",
            handlerFunc: _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(ct146Body), Encoding.UTF8, "application/json"),
            });

        var result = await tool.ListOpenPoints();
        var json = ToJsonElement(result);
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        var p = json.GetProperty("open_points")[0];
        Assert.Equal("op_1", p.GetProperty("point_id").GetString());
        Assert.Equal("guilhem", p.GetProperty("candidates")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task List_SendsGetToOpenPointsWithBearerAndFilters()
    {
        HttpRequestMessage? captured = null;
        var (tool, _) = BuildTool(token: "my-cervello-token",
            handlerFunc: req =>
            {
                captured = req;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"count\":0,\"open_points\":[]}", Encoding.UTF8, "application/json"),
                };
            });

        await tool.ListOpenPoints(kind: "speaker", recording: "rec://r1");

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Contains("/open-points", captured.RequestUri!.ToString());
        Assert.Contains("kind=speaker", captured.RequestUri.ToString());
        Assert.Contains("recording=rec", captured.RequestUri.ToString());
        Assert.Equal("Bearer my-cervello-token", captured.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Answer_PostsToAnswerRouteWithBodyAndBearer()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var (tool, _) = BuildTool(token: "cervello-tok",
            handlerFunc: req =>
            {
                captured = req;
                capturedBody = req.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"point_id\":\"op_1\",\"status\":\"applied\",\"basis\":\"human://op_1\"}",
                        Encoding.UTF8, "application/json"),
                };
            });

        var result = await tool.AnswerOpenPoint("op_1", "select", "guilhem");
        var json = ToJsonElement(result);

        Assert.Equal("applied", json.GetProperty("status").GetString());
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("/open-points/op_1/answer", captured.RequestUri!.ToString());
        Assert.Equal("Bearer cervello-tok", captured.Headers.Authorization?.ToString());
        Assert.Contains("\"mode\":\"select\"", capturedBody);
        Assert.Contains("\"value\":\"guilhem\"", capturedBody);
    }

    // ── structural ──────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("ListOpenPoints", "cervello_open_points_list")]
    [InlineData("AnswerOpenPoint", "cervello_open_points_answer")]
    public void ToolMethods_CarryCorrectMcpNames(string method, string toolName)
    {
        var m = typeof(BridgeOpenPointsTools).GetMethod(method,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(m);
        var attr = m!.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
            .Cast<ModelContextProtocol.Server.McpServerToolAttribute>().FirstOrDefault();
        Assert.NotNull(attr);
        Assert.Equal(toolName, attr!.Name);
    }

    [Fact]
    public void ToolType_IsDecorated()
    {
        Assert.Single(typeof(BridgeOpenPointsTools).GetCustomAttributes(
            typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), false));
    }

    // ── ISOLATION: the shared legacy bridge bearer must NOT reach cervello (ACCESS.md §2) ────────
    [Fact]
    public async Task SharedLegacyBearer_CannotReach_CervelloTools()
    {
        var (tool, handler) = BuildTool(token: "tok");
        // Override the ambient auth to the SHARED legacy bearer (LegacyScopes.All), which
        // deliberately excludes bridge:cervello:*. A cervello call MUST be refused.
        BridgeAuthState.CurrentAuth = new BridgeAuthContext
        {
            Mode = "bearer",
            Subject = "legacy-bearer",
            Scopes = LegacyScopes.All,
            RawToken = "shared-bearer",
        };

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tool.ListOpenPoints());
        Assert.Contains("insufficient_scope", ex.Message);
        Assert.Equal(0, handler.CallCount); // never reached CT146
    }

    // ── helpers ───────────────────────────────────────────────────────────────
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

    private static (BridgeOpenPointsTools tool, MockHttpMessageHandler handler) BuildTool(
        string token,
        bool exposed = true,
        Func<HttpRequestMessage, HttpResponseMessage>? handlerFunc = null)
    {
        var handler = new MockHttpMessageHandler(handlerFunc);
        var services = new ServiceCollection();
        services.AddHttpClient("cervello-open-points").ConfigurePrimaryHttpMessageHandler(() => handler);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        var cfg = new BridgeConfig
        {
            CervelloOpenPointsToken = token,
            CervelloOpenPointsUrl = "http://localhost:8147",
            CervelloExposed = exposed,
            BridgeBearerToken = "test-bearer",
        };

        // The cervello-scoped credential (ACCESS.md §2): a JWT carrying the cervello scopes — NOT
        // the shared legacy bearer (which deliberately lacks bridge:cervello:*, see the isolation test).
        BridgeAuthState.CurrentAuth = CervelloScopedAuth();

        var auth = new AuthService(cfg, new BridgeRateLimiter());
        var audit = new AuditService(cfg, NullLogger<AuditService>.Instance);
        return (new BridgeOpenPointsTools(auth, audit, cfg, factory), handler);
    }

    private static BridgeAuthContext CervelloScopedAuth() => new()
    {
        Mode = "jwt",
        Subject = "cervello-project",
        Scopes = new HashSet<string>(StringComparer.Ordinal)
        {
            AuthService.CervelloReadScope,
            AuthService.CervelloDepositScope,
        },
        RawToken = "cervello-scoped-jwt",
    };

    private static JsonElement ToJsonElement(object result)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement;
    }
}
