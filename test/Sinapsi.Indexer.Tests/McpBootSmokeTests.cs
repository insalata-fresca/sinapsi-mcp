// ---------------------------------------------------------------------------
// McpBootSmokeTests - the RUNTIME boot smoke that was missing.
//
// The capability-composed image deployed healthy on HTTP + indexing but its MCP
// host registered ZERO tools at runtime: tools/list returned -32601 and the
// fleet's read surface (search_index / semantic_search / get_learning) went
// down. The cause was invisible to a compile AND to the reflection-only
// ToolSurfaceTests: the production WithTools call bound the wrong overload and
// registered nothing. Reflecting over the tool CLASSES (ToolSurfaceTests) proves
// the tools EXIST; it does NOT prove the MCP HOST actually SERVES them.
//
// This suite closes that gap. It BOOTS the real MCP composition
// (McpComposition.AddIndexerMcp + MapMcp - the exact registration Program.cs
// uses) over an in-process TestServer, backed by fake store/embedder (no
// Postgres, no ONNX, no NATS), and asserts tools/list over HTTP returns the
// capability-selected tool set. If the overload-resolution footgun is ever
// reintroduced, ExposesSearchTools_WithDefaults goes red instead of the fleet.
//
// Scenarios (mirror the MI-fix acceptance criteria):
//   * defaults ON  -> search_index + semantic_search + get_learning + publish_learning
//   * LEARN_PUBLISH=false -> publish_learning ABSENT, search tools present
//   * NATS isolated (index off, search only) -> search tools still served
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

public sealed class McpBootSmokeTests
{
    // Build a TestServer hosting the REAL MCP composition for the given caps,
    // backed by fakes so no Postgres/ONNX/NATS is touched. Mirrors Program.cs:
    // AddIndexerMcp(caps) + MapMcp("/mcp").
    private static TestServer BuildMcpHost(IndexerCapabilities caps)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                // Seams the tools depend on (constructor-injected at call time).
                services.AddSingleton<IIndexStore>(new ControllableIndexStore(
                    (q, src, kind, lim, ct) =>
                        Task.FromResult<IReadOnlyList<SearchHit>>(Array.Empty<SearchHit>())));
                services.AddSingleton<IEmbedder>(new FakeEmbedder());
                // The real production tool-registration path under test.
                services.AddIndexerMcp(caps);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
            });

        return new TestServer(builder);
    }

    // Issue a stateless streamable-HTTP JSON-RPC tools/list and return the tool
    // names. Stateless transport (o.Stateless = true) serves tools/list without a
    // prior initialize handshake - exactly how the fronting proxy calls it.
    private static async Task<IReadOnlyList<string>> ListToolNamesAsync(TestServer server)
    {
        using var client = server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");

        using var resp = await client.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        resp.EnsureSuccessStatusCode();

        var json = ExtractJson(raw);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // A registered-tools host returns result.tools[]; a zero-tool host returns
        // an error object (-32601 "Method 'tools/list' is not available"). Surface
        // the error so a regression fails LOUD rather than as an empty list.
        if (root.TryGetProperty("error", out var err))
            throw new Xunit.Sdk.XunitException(
                "tools/list returned an error (MCP host registered NO tools): "
                + err.GetRawText());

        Assert.True(root.TryGetProperty("result", out var result), "response has no result: " + json);
        Assert.True(result.TryGetProperty("tools", out var tools), "result has no tools: " + json);

        var names = new List<string>();
        foreach (var t in tools.EnumerateArray())
            names.Add(t.GetProperty("name").GetString()!);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    // The stateless transport replies as SSE (text/event-stream): one or more
    // "data: {json}" lines. Pull the JSON payload out (fall back to the raw body
    // if a plain application/json body is returned instead).
    private static string ExtractJson(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
                return trimmed["data:".Length..].Trim();
        }
        return body.Trim();
    }

    [Fact]
    public async Task ExposesAllFourTools_WithDefaults()
    {
        // Defaults = today's bundled behaviour: index+search.mcp+search.http+learn_publish on.
        var caps = new IndexerCapabilities(
            index: true, searchMcp: true, searchHttp: true, learnPublish: true, natsIsolated: false);
        using var server = BuildMcpHost(caps);

        var names = await ListToolNamesAsync(server);

        Assert.Equal(
            new[] { "get_learning", "publish_learning", "search_index", "semantic_search" },
            names);
    }

    [Fact]
    public async Task SearchToolsPresent_PublishAbsent_WhenLearnPublishOff()
    {
        // LEARN_PUBLISH=false -> publish_learning must be ABSENT, search tools present.
        var caps = new IndexerCapabilities(
            index: true, searchMcp: true, searchHttp: true, learnPublish: false, natsIsolated: false);
        using var server = BuildMcpHost(caps);

        var names = await ListToolNamesAsync(server);

        Assert.Equal(
            new[] { "get_learning", "search_index", "semantic_search" },
            names);
        Assert.DoesNotContain("publish_learning", names);
    }

    [Fact]
    public async Task SearchToolsStillServed_WhenNatsIsolated()
    {
        // S50: an isolated tenant (index off, NATS isolated) still SERVES the search
        // read surface over MCP - no NATS connection, but the tools are registered.
        var caps = new IndexerCapabilities(
            index: false, searchMcp: true, searchHttp: true, learnPublish: false, natsIsolated: true);
        using var server = BuildMcpHost(caps);

        var names = await ListToolNamesAsync(server);

        Assert.Contains("search_index", names);
        Assert.Contains("semantic_search", names);
        Assert.Contains("get_learning", names);
        Assert.DoesNotContain("publish_learning", names);
    }
}
