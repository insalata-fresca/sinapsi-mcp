using System.Net;
using System.Text;
using Metabase.Mcp.Api;
using Xunit;

namespace Metabase.Mcp.Tests;

/// <summary>
/// Verifies the Metabase adapter shapes the REST request (path + method) and surfaces a
/// non-2xx upstream response as a <see cref="MetabaseApiException"/> carrying the real
/// status + body. Uses a scripted fake transport — no live instance.
/// </summary>
public sealed class MetabaseClientTests
{
    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path)> Calls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (MetabaseClient client, ScriptedHandler handler) Build(HttpStatusCode status, string body)
    {
        var handler = new ScriptedHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://metrics.example.com/") };
        return (new MetabaseClient(http), handler);
    }

    [Fact]
    public async Task ListDatabases_gets_the_database_path()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"data":[]}""");

        var res = await client.ListDatabasesAsync(CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("/api/database", call.Path);
        Assert.True(res.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task ListCollections_gets_the_collection_path()
    {
        var (client, handler) = Build(HttpStatusCode.OK, "[]");
        await client.ListCollectionsAsync(CancellationToken.None);
        Assert.Equal("/api/collection", Assert.Single(handler.Calls).Path);
    }

    [Fact]
    public async Task GetCard_gets_the_card_by_id_path()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"id":7}""");
        await client.GetCardAsync(7, CancellationToken.None);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("/api/card/7", call.Path);
    }

    [Fact]
    public async Task Non_2xx_surfaces_status_and_body_as_MetabaseApiException()
    {
        var (client, _) = Build(HttpStatusCode.Unauthorized, """{"message":"invalid api key"}""");

        var ex = await Assert.ThrowsAsync<MetabaseApiException>(
            () => client.ListCardsAsync(CancellationToken.None));

        Assert.Equal(401, ex.Status);
        Assert.Contains("invalid api key", ex.Message);
    }
}
