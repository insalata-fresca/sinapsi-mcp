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
/// B4b-8 career_search tests (intentionally-CHANGED tool — NOT held to Python parity).
///
/// Tests cover:
///   1. empty CAREER_SEARCH_TOKEN → {status:"not_configured"} BEFORE any I/O.
///   2. Transport failure → {status:"unreachable"}.
///   3. HTTP 400 → {status:"bad_request", http_status:400}.
///   4. HTTP 401 → {status:"unauthorized", http_status:401}.
///   5. HTTP 404 → {status:"route_disabled", http_status:404}.
///   6. HTTP 500 → {status:"error", http_status:500}.
///   7. non-JSON body → {status:"bad_response"}.
///   8. Success: map indexer camelCase → flat hits (score→rank, drop kind/scope),
///      count = resultCount.
///   9. Structural: BridgeCareerSearchTools has CareerSearch + correct MCP name.
///
/// HTTP mocking: uses MockHttpMessageHandler (inline class) to intercept calls.
/// The DI pattern: build an IHttpClientFactory via ServiceCollection.AddHttpClient.
/// Career_search tool is instantiated directly (no ASP.NET host needed).
/// </summary>
public sealed class CareerSearchToolsTests
{
    // ── Status envelope tests ──────────────────────────────────────────────────

    [Fact]
    public async Task EmptyToken_ReturnsNotConfigured_BeforeAnyIo()
    {
        // CAREER_SEARCH_TOKEN empty → {status:"not_configured"} without any HTTP call.
        var (tool, handler) = BuildTool(token: "");

        var result = await tool.CareerSearch("some query");
        var json = ToJsonElement(result);

        Assert.Equal("not_configured", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("note", out _));
        // No HTTP call must have been made.
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TransportFailure_ReturnsUnreachable()
    {
        // Simulate transport-level failure (e.g. connection refused).
        var (tool, _) = BuildTool(
            token: "valid-token",
            handlerFunc: _ => throw new HttpRequestException("Connection refused"));

        var result = await tool.CareerSearch("query");
        var json = ToJsonElement(result);

        Assert.Equal("unreachable", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("note", out _));
    }

    [Theory]
    [InlineData(400, "bad_request")]
    [InlineData(401, "unauthorized")]
    [InlineData(404, "route_disabled")]
    [InlineData(500, "error")]
    [InlineData(503, "error")]
    [InlineData(429, "error")]
    public async Task NonSuccessStatus_ReturnsCorrectEnvelope(int httpStatus, string expectedStatus)
    {
        // Non-200 HTTP responses → structured status envelope with http_status.
        var (tool, _) = BuildTool(
            token: "valid-token",
            handlerFunc: _ => new HttpResponseMessage((HttpStatusCode)httpStatus)
            {
                Content = new StringContent("{\"error\":\"something\"}"),
            });

        var result = await tool.CareerSearch("query");
        var json = ToJsonElement(result);

        Assert.Equal(expectedStatus, json.GetProperty("status").GetString());
        Assert.Equal(httpStatus, json.GetProperty("http_status").GetInt32());
        Assert.True(json.TryGetProperty("note", out _));
    }

    [Fact]
    public async Task NonJsonBody_ReturnsBadResponse()
    {
        // 200 response but non-JSON body → {status:"bad_response"}.
        var (tool, _) = BuildTool(
            token: "valid-token",
            handlerFunc: _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("this is not json at all", Encoding.UTF8, "text/plain"),
            });

        var result = await tool.CareerSearch("query");
        var json = ToJsonElement(result);

        Assert.Equal("bad_response", json.GetProperty("status").GetString());
    }

    // ── Success: indexer → flat hit shape mapping ─────────────────────────────

    [Fact]
    public async Task Success_MapsIndexerResponse_ToFlatHits()
    {
        // Indexer 200 response (camelCase) → preserved Python flat hit shape.
        // score → rank, kind + scope are DROPPED.
        var indexerResponse = new
        {
            query       = "career query",
            resultCount = 2,
            results     = new[]
            {
                new { source = "career/s1", path = "/docs/a.md", kind = "document",
                      title = "Doc A", scope = "career",  snippet = "snippet A", score = 0.9 },
                new { source = "career/s2", path = "/docs/b.md", kind = "section",
                      title = "Doc B", scope = "career2", snippet = "snippet B", score = 0.7 },
            },
        };

        var (tool, _) = BuildTool(
            token: "valid-token",
            handlerFunc: _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(indexerResponse),
                    Encoding.UTF8,
                    "application/json"),
            });

        var result = await tool.CareerSearch("career query", limit: 10);
        var json = ToJsonElement(result);

        // Top-level: query, count, hits.
        Assert.Equal("career query", json.GetProperty("query").GetString());
        Assert.Equal(2, json.GetProperty("count").GetInt32());

        var hits = json.GetProperty("hits");
        Assert.Equal(2, hits.GetArrayLength());

        // First hit: title, path, snippet, rank (=score), source.
        var h0 = hits[0];
        Assert.Equal("Doc A",      h0.GetProperty("title").GetString());
        Assert.Equal("/docs/a.md", h0.GetProperty("path").GetString());
        Assert.Equal("snippet A",  h0.GetProperty("snippet").GetString());
        Assert.Equal(0.9,          h0.GetProperty("rank").GetDouble(), precision: 6);
        Assert.Equal("career/s1",  h0.GetProperty("source").GetString());

        // kind and scope must NOT be present in the output (dropped).
        Assert.False(h0.TryGetProperty("kind",  out _), "kind must be dropped");
        Assert.False(h0.TryGetProperty("scope", out _), "scope must be dropped");

        // Second hit: rank = 0.7.
        var h1 = hits[1];
        Assert.Equal(0.7, h1.GetProperty("rank").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task Success_EmptyResults_ReturnsEmptyHits()
    {
        // Indexer returns 0 results.
        var indexerResponse = new { query = "q", resultCount = 0, results = Array.Empty<object>() };
        var (tool, _) = BuildTool(
            token: "valid-token",
            handlerFunc: _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(indexerResponse),
                    Encoding.UTF8,
                    "application/json"),
            });

        var result = await tool.CareerSearch("q");
        var json = ToJsonElement(result);

        Assert.Equal(0, json.GetProperty("count").GetInt32());
        Assert.Equal(0, json.GetProperty("hits").GetArrayLength());
    }

    [Fact]
    public async Task Success_CountEqualsResultCount_NotHitsLength()
    {
        // count = resultCount from the indexer (even if results array length differs).
        // This preserves the Python shape: count = data.get("count", len(hits)) for old
        // Python; C# uses resultCount directly.
        var indexerResponse = new { query = "q", resultCount = 42, results = new[] {
            new { source = "s", path = "p", kind = "k", title = "t", scope = "sc", snippet = "sn", score = 1.0 }
        }};
        var (tool, _) = BuildTool(
            token: "valid-token",
            handlerFunc: _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(indexerResponse),
                    Encoding.UTF8,
                    "application/json"),
            });

        var result = await tool.CareerSearch("q");
        var json = ToJsonElement(result);

        // count comes from resultCount, not hits.length.
        Assert.Equal(42, json.GetProperty("count").GetInt32());
        Assert.Equal(1, json.GetProperty("hits").GetArrayLength());
    }

    [Fact]
    public async Task RequestUrl_IncludesQueryAndLimit()
    {
        // Verify the HTTP call reaches the right URL with q= and limit= params.
        string? capturedUrl = null;
        var (tool, _) = BuildTool(
            token: "tok",
            handlerFunc: req =>
            {
                capturedUrl = req.RequestUri?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { query = "test", resultCount = 0, results = Array.Empty<object>() }),
                        Encoding.UTF8, "application/json"),
                };
            });

        await tool.CareerSearch("test query", limit: 5);

        Assert.NotNull(capturedUrl);
        // URL may be decoded by the HttpClient (space as %20 or literal space).
        Assert.True(
            capturedUrl!.Contains("q=test%20query") || capturedUrl.Contains("q=test query"),
            $"Expected URL to contain encoded or literal query param, got: {capturedUrl}");
        Assert.Contains("limit=5", capturedUrl);
        Assert.Contains("/search", capturedUrl);
    }

    [Fact]
    public async Task RequestHeader_HasBearerToken()
    {
        // Verify Authorization: Bearer {token} header is sent.
        string? capturedAuth = null;
        var (tool, _) = BuildTool(
            token: "my-career-token",
            handlerFunc: req =>
            {
                capturedAuth = req.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { query = "q", resultCount = 0, results = Array.Empty<object>() }),
                        Encoding.UTF8, "application/json"),
                };
            });

        await tool.CareerSearch("q");

        Assert.Equal("Bearer my-career-token", capturedAuth);
    }

    // ── Structural tests ──────────────────────────────────────────────────────

    [Fact]
    public void BridgeCareerSearchTools_ExposesCareerSearch()
    {
        var method = typeof(BridgeCareerSearchTools).GetMethod(
            "CareerSearch",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Fact]
    public void BridgeCareerSearchTools_ToolNameIsCareerSearch()
    {
        var method = typeof(BridgeCareerSearchTools).GetMethod(
            "CareerSearch",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var toolAttr = method!.GetCustomAttributes(
            typeof(ModelContextProtocol.Server.McpServerToolAttribute), inherit: false)
            .Cast<ModelContextProtocol.Server.McpServerToolAttribute>()
            .FirstOrDefault();
        Assert.NotNull(toolAttr);
        Assert.Equal("career_search", toolAttr!.Name);
    }

    [Fact]
    public void BridgeCareerSearchTools_IsDecoratedWithMcpServerToolType()
    {
        var attr = typeof(BridgeCareerSearchTools).GetCustomAttributes(
            typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), inherit: false);
        Assert.Single(attr);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class MockHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? func = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (func is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            return Task.FromResult(func(request));
        }
    }

    private static (BridgeCareerSearchTools tool, MockHttpMessageHandler handler) BuildTool(
        string token,
        Func<HttpRequestMessage, HttpResponseMessage>? handlerFunc = null)
    {
        var handler = new MockHttpMessageHandler(handlerFunc);

        // Build a real IHttpClientFactory via DI — the correct way to create named clients.
        var services = new ServiceCollection();
        services.AddHttpClient("career-search")
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        var cfg = new BridgeConfig
        {
            CareerSearchToken = token,
            CareerSearchUrl   = "http://localhost:8009",  // test base URL
            BridgeBearerToken = "test-bearer",
        };

        // Auth context stash for tests — inject a valid bearer auth context.
        BridgeAuthState.CurrentAuth = new BridgeAuthContext
        {
            Mode     = "bearer",
            Subject  = "test-user",
            Scopes   = LegacyScopes.All,
            RawToken = "test-bearer",
        };

        var limiter = new BridgeRateLimiter();
        var auth    = new AuthService(cfg, limiter);
        var logger  = NullLogger<AuditService>.Instance;
        var audit   = new AuditService(cfg, logger);

        var tool = new BridgeCareerSearchTools(auth, audit, cfg, factory);
        return (tool, handler);
    }

    private static JsonElement ToJsonElement(object result)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement;
    }
}
