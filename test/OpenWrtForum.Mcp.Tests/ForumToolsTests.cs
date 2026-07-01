using System.Net;
using System.Text;
using System.Text.Json;
using OpenWrtForum.Mcp;
using Xunit;

namespace OpenWrtForum.Mcp.Tests;

/// <summary>
/// Drives the read-side tool surface through a stub <see cref="HttpMessageHandler"/>
/// that returns canned Discourse JSON, then asserts the shape the tools project.
/// No network, no live forum.
/// </summary>
public sealed class ForumToolsTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _respond;
        public readonly List<string> Requests = new();
        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var (code, body) = _respond(request);
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static DiscourseClient Client(StubHandler h) =>
        new(new DiscourseOptions("https://forum.example.com", "", "", 30_000), h);

    [Fact]
    public async Task ListCategories_Projects_Id_Name_Slug_Count()
    {
        var h = new StubHandler(_ => (HttpStatusCode.OK, """
        { "category_list": { "categories": [
            { "id": 7, "name": "General", "slug": "general", "topic_count": 42 },
            { "id": 8, "name": "Dev",     "slug": "devel",   "topic_count": 9 }
        ] } }
        """));
        var json = await ForumTools.ListCategories(Client(h), CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal(7, arr[0].GetProperty("id").GetInt32());
        Assert.Equal("General", arr[0].GetProperty("name").GetString());
        Assert.Equal("devel", arr[1].GetProperty("slug").GetString());
        Assert.Equal(9, arr[1].GetProperty("topic_count").GetInt32());
        Assert.Contains("/categories.json", h.Requests[0]);
    }

    [Fact]
    public async Task Search_Builds_Urls_And_Caps_And_Truncates()
    {
        var longBlurb = new string('x', 600);
        var h = new StubHandler(_ => (HttpStatusCode.OK, $$"""
        {
          "topics": [ { "id": 100, "slug": "hello-world", "title": "Hello",
                        "reply_count": 3, "views": 50, "created_at": "2024-01-02",
                        "tags": ["a","b"] } ],
          "posts":  [ { "id": 9, "topic_id": 100, "post_number": 4,
                        "username": "bob", "blurb": "{{longBlurb}}" } ]
        }
        """));
        var json = await ForumTools.Search(Client(h), "ath12k #devel", 2, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var topic = root.GetProperty("topics")[0];
        Assert.Equal("https://forum.example.com/t/hello-world/100", topic.GetProperty("url").GetString());
        Assert.Equal(2, topic.GetProperty("tags").GetArrayLength());

        var post = root.GetProperty("posts")[0];
        Assert.Equal("https://forum.example.com/t/100/4", post.GetProperty("url").GetString());
        Assert.Equal(400, post.GetProperty("excerpt").GetString()!.Length); // truncated to 400

        // query string carries q + page
        Assert.Contains("q=ath12k", h.Requests[0]);
        Assert.Contains("page=2", h.Requests[0]);
    }

    [Fact]
    public async Task Search_Falls_Back_To_Id_When_Slug_Missing()
    {
        var h = new StubHandler(_ => (HttpStatusCode.OK, """
        { "topics": [ { "id": 55, "title": "No slug" } ], "posts": [] }
        """));
        var json = await ForumTools.Search(Client(h), "q", 1, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("https://forum.example.com/t/55/55",
            doc.RootElement.GetProperty("topics")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task GetTopic_Projects_Posts_And_Truncates_Body()
    {
        var longCooked = new string('y', 4000);
        var h = new StubHandler(_ => (HttpStatusCode.OK, $$"""
        {
          "id": 200, "slug": "topic-x", "title": "Topic X",
          "created_at": "2024-03-03", "reply_count": 1, "views": 7,
          "category_id": 8, "tags": ["k"],
          "post_stream": { "posts": [
            { "post_number": 1, "username": "carol", "created_at": "2024-03-03", "cooked": "{{longCooked}}" }
          ] }
        }
        """));
        var json = await ForumTools.GetTopic(Client(h), 200, 0, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("https://forum.example.com/t/topic-x/200", root.GetProperty("url").GetString());
        Assert.Equal(8, root.GetProperty("category_id").GetInt32());
        var p0 = root.GetProperty("posts")[0];
        Assert.Equal("carol", p0.GetProperty("author").GetString());
        Assert.Equal(3000, p0.GetProperty("body").GetString()!.Length); // truncated to 3000
        // page 0 omits the ?page query
        Assert.DoesNotContain("page=", h.Requests[0]);
    }

    [Fact]
    public async Task GetLatest_Uses_Category_Path_When_Slug_Given()
    {
        var h = new StubHandler(_ => (HttpStatusCode.OK, """
        { "topic_list": { "topics": [
            { "id": 1, "slug": "t1", "title": "T1", "reply_count": 0, "views": 1, "last_posted_at": "2024-05-05" }
        ] } }
        """));
        var json = await ForumTools.GetLatest(Client(h), "devel", 0, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("https://forum.example.com/t/t1/1",
            doc.RootElement[0].GetProperty("url").GetString());
        Assert.Contains("/c/devel/l/latest.json", h.Requests[0]);
    }

    [Fact]
    public async Task GetLatest_Uses_Plain_Latest_When_No_Slug()
    {
        var h = new StubHandler(_ => (HttpStatusCode.OK, """{ "topic_list": { "topics": [] } }"""));
        await ForumTools.GetLatest(Client(h), null, 0, CancellationToken.None);
        Assert.Contains("/latest.json", h.Requests[0]);
        Assert.DoesNotContain("/c/", h.Requests[0]);
    }

    [Fact]
    public async Task Tolerant_Accessors_Survive_Wrong_Types_And_Nulls()
    {
        // id arrives as a string, topic_count as null — strict GetValue<int> would throw.
        var h = new StubHandler(_ => (HttpStatusCode.OK, """
        { "category_list": { "categories": [
            { "id": "not-a-number", "name": null, "slug": "s", "topic_count": null }
        ] } }
        """));
        var json = await ForumTools.ListCategories(Client(h), CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var c0 = doc.RootElement[0];
        Assert.Equal(0, c0.GetProperty("id").GetInt32());
        Assert.Equal("", c0.GetProperty("name").GetString());
        Assert.Equal("s", c0.GetProperty("slug").GetString());
        Assert.Equal(0, c0.GetProperty("topic_count").GetInt32());
    }

    [Fact]
    public async Task Http_Error_Throws_Structured_Envelope()
    {
        var h = new StubHandler(_ => (HttpStatusCode.NotFound, """{ "errors": ["nope"] }"""));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ForumTools.ListCategories(Client(h), CancellationToken.None));
        using var doc = JsonDocument.Parse(ex.Message);
        Assert.Equal(404, doc.RootElement.GetProperty("status_code").GetInt32());
        Assert.Equal("discourse 404", doc.RootElement.GetProperty("error").GetString());
        // body was JSON-parseable → preserved as an object, not a string
        Assert.True(doc.RootElement.GetProperty("body").TryGetProperty("errors", out _));
    }
}
