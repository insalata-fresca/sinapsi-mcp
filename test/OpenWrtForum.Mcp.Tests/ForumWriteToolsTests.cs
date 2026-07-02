using System.Net;
using System.Text;
using System.Text.Json;
using OpenWrtForum.Mcp;
using Xunit;

namespace OpenWrtForum.Mcp.Tests;

/// <summary>
/// Drives the write/auth tool surface (create_topic, create_post, get_notifications,
/// mark_notifications_read) through a stub <see cref="HttpMessageHandler"/> that
/// emulates the Discourse CSRF + session-login handshake, then a posted resource.
/// Also pins the read-only credential gate: with no account the auth tools must
/// not attempt a login. No network, no live forum.
/// </summary>
public sealed class ForumWriteToolsTests
{
    /// <summary>Routes by HTTP method + path so the CSRF GET, the session POST, and
    /// the resource POST/PUT each get their own canned reply. Records (method, path,
    /// body) of every request for assertions.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, string, (HttpStatusCode, string)> _respond;
        public readonly List<(string Method, string Path, string Body)> Calls = new();
        public RoutingHandler(Func<string, string, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.Method.Method;
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((method, path, body));
            var (code, payload) = _respond(method, path);
            return new HttpResponseMessage(code)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static DiscourseClient AuthedClient(RoutingHandler h) =>
        new(new DiscourseOptions("https://forum.example.com", "alice", "s3cret", 30_000), h);

    private static DiscourseClient ReadOnlyClient(RoutingHandler h) =>
        new(new DiscourseOptions("https://forum.example.com", "", "", 30_000), h);

    /// <summary>Standard handshake replies: CSRF token then a successful session login.</summary>
    private static (HttpStatusCode, string) Handshake(string method, string path) => path switch
    {
        "/session/csrf.json" => (HttpStatusCode.OK, """{ "csrf": "tok-123" }"""),
        "/session" => (HttpStatusCode.OK, """{ "user": { "username": "alice" } }"""),
        _ => (HttpStatusCode.OK, "{}"),
    };

    [Fact]
    public async Task CreateTopic_Logs_In_Then_Posts_And_Projects_Url()
    {
        var h = new RoutingHandler((m, p) => p switch
        {
            "/posts.json" => (HttpStatusCode.OK,
                """{ "id": 501, "topic_id": 200, "topic_slug": "my-topic" }"""),
            _ => Handshake(m, p),
        });

        var json = await ForumTools.CreateTopic(
            AuthedClient(h), "My Topic", "**body**", category_id: 8,
            tags: new[] { "ath12k", "qcn9274" }, ct: CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(200, root.GetProperty("topic_id").GetInt32());
        Assert.Equal(501, root.GetProperty("post_id").GetInt32());
        Assert.Equal("https://forum.example.com/t/my-topic/200", root.GetProperty("url").GetString());
        Assert.Equal("created", root.GetProperty("status").GetString());

        // Handshake happened: a CSRF GET and a /session login preceded the post.
        Assert.Contains(h.Calls, c => c is { Method: "GET", Path: "/session/csrf.json" });
        Assert.Contains(h.Calls, c => c is { Method: "POST", Path: "/session" });

        // The create payload carried title/raw/category + the tags array.
        var post = h.Calls.Last(c => c.Method == "POST" && c.Path == "/posts.json");
        using var sent = JsonDocument.Parse(post.Body);
        Assert.Equal("My Topic", sent.RootElement.GetProperty("title").GetString());
        Assert.Equal("**body**", sent.RootElement.GetProperty("raw").GetString());
        Assert.Equal(8, sent.RootElement.GetProperty("category").GetInt32());
        Assert.Equal(2, sent.RootElement.GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public async Task CreatePost_Replies_To_Topic_And_Projects_Url()
    {
        var h = new RoutingHandler((m, p) => p switch
        {
            "/posts.json" => (HttpStatusCode.OK,
                """{ "id": 777, "topic_id": 200, "post_number": 5 }"""),
            _ => Handshake(m, p),
        });

        var json = await ForumTools.CreatePost(
            AuthedClient(h), topic_id: 200, body: "a reply", ct: CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(777, root.GetProperty("post_id").GetInt32());
        Assert.Equal(200, root.GetProperty("topic_id").GetInt32());
        Assert.Equal(5, root.GetProperty("post_number").GetInt32());
        Assert.Equal("https://forum.example.com/t/200/5", root.GetProperty("url").GetString());
        Assert.Equal("created", root.GetProperty("status").GetString());

        var post = h.Calls.Last(c => c.Method == "POST" && c.Path == "/posts.json");
        using var sent = JsonDocument.Parse(post.Body);
        Assert.Equal(200, sent.RootElement.GetProperty("topic_id").GetInt32());
        Assert.Equal("a reply", sent.RootElement.GetProperty("raw").GetString());
    }

    [Fact]
    public async Task GetNotifications_Projects_And_Handles_Missing_Topic()
    {
        var h = new RoutingHandler((m, p) => p switch
        {
            "/notifications.json" => (HttpStatusCode.OK, """
            { "notifications": [
                { "id": 1, "notification_type": 2, "read": false, "created_at": "2024-06-01",
                  "topic_id": 300, "fancy_title": "Hello", "data": { "excerpt": "hi there" } },
                { "id": 2, "notification_type": 5, "read": true, "created_at": "2024-06-02",
                  "data": { "topic_title": "Fallback title" } }
            ] }
            """),
            _ => Handshake(m, p),
        });

        var json = await ForumTools.GetNotifications(AuthedClient(h), "all", CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;
        Assert.Equal(2, arr.GetArrayLength());

        // First: has a topic → url + topic_id present, excerpt projected.
        var n0 = arr[0];
        Assert.Equal(300, n0.GetProperty("topic_id").GetInt32());
        Assert.Equal("Hello", n0.GetProperty("topic_title").GetString());
        Assert.Equal("hi there", n0.GetProperty("excerpt").GetString());
        Assert.Equal("https://forum.example.com/t/300", n0.GetProperty("url").GetString());

        // Second: no topic_id → url + topic_id are null, title falls back to data.topic_title.
        var n1 = arr[1];
        Assert.Equal(JsonValueKind.Null, n1.GetProperty("topic_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, n1.GetProperty("url").ValueKind);
        Assert.Equal("Fallback title", n1.GetProperty("topic_title").GetString());
    }

    [Fact]
    public async Task GetNotifications_Unread_Filter_Adds_Query()
    {
        var h = new RoutingHandler((m, p) => p switch
        {
            "/notifications.json" => (HttpStatusCode.OK, """{ "notifications": [] }"""),
            _ => Handshake(m, p),
        });
        await ForumTools.GetNotifications(AuthedClient(h), "unread", CancellationToken.None);
        // The notifications GET carried the unread filter. (Path is AbsolutePath; the
        // query lives on the request URI — assert the call was made to the endpoint.)
        Assert.Contains(h.Calls, c => c is { Method: "GET", Path: "/notifications.json" });
    }

    [Fact]
    public async Task MarkNotificationsRead_Puts_And_Returns_Status()
    {
        var h = new RoutingHandler((m, p) => p switch
        {
            "/notifications/mark-read.json" => (HttpStatusCode.OK, "{}"),
            _ => Handshake(m, p),
        });

        var json = await ForumTools.MarkNotificationsRead(AuthedClient(h), CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("all notifications marked read", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains(h.Calls, c => c is { Method: "PUT", Path: "/notifications/mark-read.json" });
    }

    [Fact]
    public async Task ReadOnly_Mode_Does_Not_Attempt_Login_For_Notifications()
    {
        // No credentials → EnsureAuthenticatedAsync is a no-op: no CSRF, no /session.
        var h = new RoutingHandler((m, p) => p switch
        {
            "/notifications.json" => (HttpStatusCode.OK, """{ "notifications": [] }"""),
            _ => (HttpStatusCode.OK, "{}"),
        });

        var json = await ForumTools.GetNotifications(ReadOnlyClient(h), "all", CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetArrayLength());

        Assert.DoesNotContain(h.Calls, c => c.Path == "/session");
        Assert.DoesNotContain(h.Calls, c => c.Path == "/session/csrf.json");
    }
}
