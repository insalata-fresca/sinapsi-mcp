using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace OpenWrtForum.Mcp;

[McpServerToolType]
public sealed class ForumTools
{
    private static readonly JsonSerializerOptions _ind =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    /// <summary>Build the structured <c>{ ok: false, error }</c> envelope every tool
    /// returns on an input-validation failure — emitted BEFORE any HTTP call. The
    /// <paramref name="reason"/> is a validation message (no upstream text), but it
    /// is routed through <see cref="OpenWrtForumErrors.Sanitize"/> for uniformity so
    /// there is exactly one error-shaping choke point in the tool surface.</summary>
    private static string ValidationError(string reason) =>
        new JsonObject { ["ok"] = false, ["error"] = OpenWrtForumErrors.Sanitize(reason) }.ToJsonString(_ind);

    // Tolerant type accessors — Discourse occasionally returns null or a
    // differently-typed field, and JsonNode.GetValue<T>() is a strict cast that
    // throws InvalidOperationException on type mismatch. These coerce defensively.
    private static string AsString(JsonNode? n) =>
        n is null ? "" : (n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : "");
    private static int AsInt(JsonNode? n) =>
        n is null ? 0 : (n.GetValueKind() == JsonValueKind.Number ? n.GetValue<int>() : 0);
    private static bool AsBool(JsonNode? n) =>
        n is null ? false : (n.GetValueKind() == JsonValueKind.True || n.GetValueKind() == JsonValueKind.False
            ? n.GetValue<bool>() : false);

    [McpServerTool(Name = "forum_list_categories")]
    [Description("List all categories on the forum")]
    public static async Task<string> ListCategories(DiscourseClient c, CancellationToken ct)
    {
        var data = await c.GetAsync("/categories.json", ct: ct);
        var cats = (data["category_list"]?["categories"] as JsonArray) ?? new JsonArray();
        var result = new JsonArray();
        foreach (var n in cats)
        {
            result.Add(new JsonObject
            {
                ["id"] = AsInt(n?["id"]),
                ["name"] = AsString(n?["name"]),
                ["slug"] = AsString(n?["slug"]),
                ["topic_count"] = AsInt(n?["topic_count"]),
            });
        }
        return result.ToJsonString(_ind);
    }

    [McpServerTool(Name = "forum_search")]
    [Description("Search topics and posts on the forum. Supports Discourse search syntax: 'ath12k #devel', 'QCN9274 order:latest', '@username', '#category-slug'")]
    public static async Task<string> Search(
        DiscourseClient c,
        [Description("Search query (Discourse syntax)")] string query,
        [Description("Page (default 1)")] int page = 1,
        CancellationToken ct = default)
    {
        // Validate every parameter BEFORE any HTTP call; a bad value short-circuits
        // to a structured error rather than a wasted round-trip.
        if (OpenWrtForumValidation.ValidateQuery(query) is { } e) return ValidationError(e);
        if (OpenWrtForumValidation.ValidatePage(page) is { } ep) return ValidationError(ep);

        var data = await c.GetAsync("/search.json", new Dictionary<string, string?>
        {
            ["q"] = query, ["page"] = page.ToString(),
        }, ct);

        var topics = (data["topics"] as JsonArray) ?? new JsonArray();
        var posts = (data["posts"] as JsonArray) ?? new JsonArray();
        var baseUrl = c.BaseUrl;

        var topicsOut = new JsonArray();
        foreach (var t in topics.Take(25))
        {
            var id = AsInt(t?["id"]);
            var slug = AsString(t?["slug"]);
            if (string.IsNullOrEmpty(slug)) slug = id.ToString();
            topicsOut.Add(new JsonObject
            {
                ["id"] = id,
                ["title"] = AsString(t?["title"]),
                ["url"] = $"{baseUrl}/t/{slug}/{id}",
                ["reply_count"] = AsInt(t?["reply_count"]),
                ["views"] = AsInt(t?["views"]),
                ["created_at"] = AsString(t?["created_at"]),
                ["tags"] = (t?["tags"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray(),
            });
        }
        var postsOut = new JsonArray();
        foreach (var p in posts.Take(10))
        {
            var topicId = AsInt(p?["topic_id"]);
            var postNum = AsInt(p?["post_number"]);
            postsOut.Add(new JsonObject
            {
                ["post_id"] = AsInt(p?["id"]),
                ["topic_id"] = topicId,
                ["author"] = AsString(p?["username"]),
                ["excerpt"] = Trim(AsString(p?["blurb"]), 400),
                ["url"] = $"{baseUrl}/t/{topicId}/{(postNum == 0 ? 1 : postNum)}",
            });
        }

        return new JsonObject { ["topics"] = topicsOut, ["posts"] = postsOut }.ToJsonString(_ind);
    }

    [McpServerTool(Name = "forum_get_topic")]
    [Description("Get a topic and all its posts by topic ID")]
    public static async Task<string> GetTopic(
        DiscourseClient c,
        [Description("Topic ID")] int topic_id,
        [Description("Post page (default 0)")] int page = 0,
        CancellationToken ct = default)
    {
        if (OpenWrtForumValidation.ValidateId(topic_id, "topic_id") is { } e) return ValidationError(e);
        if (OpenWrtForumValidation.ValidatePage(page) is { } ep) return ValidationError(ep);

        var query = page != 0 ? new Dictionary<string, string?> { ["page"] = page.ToString() } : null;
        var data = await c.GetAsync($"/t/{topic_id}.json", query, ct);

        var slug = AsString(data["slug"]);
        if (string.IsNullOrEmpty(slug)) slug = AsInt(data["id"]).ToString();

        var postsArr = (data["post_stream"]?["posts"] as JsonArray) ?? new JsonArray();
        var postsOut = new JsonArray();
        foreach (var p in postsArr)
        {
            postsOut.Add(new JsonObject
            {
                ["post_number"] = AsInt(p?["post_number"]),
                ["author"] = AsString(p?["username"]),
                ["created_at"] = AsString(p?["created_at"]),
                ["body"] = Trim(AsString(p?["cooked"]), 3000),
            });
        }

        return new JsonObject
        {
            ["id"] = AsInt(data["id"]),
            ["title"] = AsString(data["title"]),
            ["url"] = $"{c.BaseUrl}/t/{slug}/{AsInt(data["id"])}",
            ["created_at"] = AsString(data["created_at"]),
            ["reply_count"] = AsInt(data["reply_count"]),
            ["views"] = AsInt(data["views"]),
            ["category_id"] = AsInt(data["category_id"]),
            ["tags"] = (data["tags"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray(),
            ["posts"] = postsOut,
        }.ToJsonString(_ind);
    }

    [McpServerTool(Name = "forum_get_latest")]
    [Description("Get latest topics, optionally filtered by category slug")]
    public static async Task<string> GetLatest(
        DiscourseClient c,
        [Description("Category slug, e.g. 'devel'")] string? category_slug = null,
        [Description("Page (default 0)")] int page = 0,
        CancellationToken ct = default)
    {
        if (OpenWrtForumValidation.ValidateCategorySlug(category_slug) is { } e) return ValidationError(e);
        if (OpenWrtForumValidation.ValidatePage(page) is { } ep) return ValidationError(ep);

        var path = string.IsNullOrEmpty(category_slug) ? "/latest.json" : $"/c/{category_slug}/l/latest.json";
        var data = await c.GetAsync(path, new Dictionary<string, string?> { ["page"] = page.ToString() }, ct);

        var topics = (data["topic_list"]?["topics"] as JsonArray) ?? new JsonArray();
        var result = new JsonArray();
        foreach (var t in topics.Take(30))
        {
            var id = AsInt(t?["id"]);
            var slug = AsString(t?["slug"]);
            if (string.IsNullOrEmpty(slug)) slug = id.ToString();
            result.Add(new JsonObject
            {
                ["id"] = id,
                ["title"] = AsString(t?["title"]),
                ["url"] = $"{c.BaseUrl}/t/{slug}/{id}",
                ["reply_count"] = AsInt(t?["reply_count"]),
                ["views"] = AsInt(t?["views"]),
                ["last_posted_at"] = AsString(t?["last_posted_at"]),
                ["tags"] = (t?["tags"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray(),
            });
        }
        return result.ToJsonString(_ind);
    }

    [McpServerTool(Name = "forum_create_topic")]
    [Description("Create a new topic on the forum. Requires DISCOURSE_API_PASSWORD to be set.")]
    public static async Task<string> CreateTopic(
        DiscourseClient c,
        [Description("Topic title")] string title,
        [Description("Markdown body")] string body,
        [Description("Use forum_list_categories to find the ID")] int category_id,
        [Description("Optional tags")] string[]? tags = null,
        CancellationToken ct = default)
    {
        // Validate every parameter BEFORE the login handshake + POST, so a
        // malformed create never authenticates or mutates the forum.
        if (OpenWrtForumValidation.ValidateTitle(title) is { } et) return ValidationError(et);
        if (OpenWrtForumValidation.ValidateBody(body) is { } eb) return ValidationError(eb);
        if (OpenWrtForumValidation.ValidateId(category_id, "category_id") is { } ec) return ValidationError(ec);
        if (OpenWrtForumValidation.ValidateTags(tags) is { } eg) return ValidationError(eg);

        var payload = new JsonObject
        {
            ["title"] = title,
            ["raw"] = body,
            ["category"] = category_id,
        };
        if (tags is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var t in tags) arr.Add(t);
            payload["tags"] = arr;
        }
        var data = await c.PostAuthAsync("/posts.json", payload, ct);
        var topicId = AsInt(data["topic_id"]);
        var slug = AsString(data["topic_slug"]);
        if (string.IsNullOrEmpty(slug)) slug = topicId.ToString();
        return new JsonObject
        {
            ["topic_id"] = topicId,
            ["post_id"] = AsInt(data["id"]),
            ["url"] = $"{c.BaseUrl}/t/{slug}/{topicId}",
            ["status"] = "created",
        }.ToJsonString(_ind);
    }

    [McpServerTool(Name = "forum_create_post")]
    [Description("Reply to an existing topic. Requires DISCOURSE_API_PASSWORD to be set.")]
    public static async Task<string> CreatePost(
        DiscourseClient c,
        [Description("Topic ID to reply to")] int topic_id,
        [Description("Markdown body")] string body,
        CancellationToken ct = default)
    {
        if (OpenWrtForumValidation.ValidateId(topic_id, "topic_id") is { } e) return ValidationError(e);
        if (OpenWrtForumValidation.ValidateBody(body) is { } eb) return ValidationError(eb);

        var data = await c.PostAuthAsync("/posts.json", new JsonObject
        {
            ["topic_id"] = topic_id,
            ["raw"] = body,
        }, ct);

        var resTopicId = AsInt(data["topic_id"]);
        var postNum = AsInt(data["post_number"]);
        return new JsonObject
        {
            ["post_id"] = AsInt(data["id"]),
            ["topic_id"] = resTopicId,
            ["post_number"] = postNum,
            ["url"] = $"{c.BaseUrl}/t/{resTopicId}/{(postNum == 0 ? 1 : postNum)}",
            ["status"] = "created",
        }.ToJsonString(_ind);
    }

    [McpServerTool(Name = "forum_get_notifications")]
    [Description("Get notifications (replies, mentions) for the configured account")]
    public static async Task<string> GetNotifications(
        DiscourseClient c,
        [Description("'all' | 'unread' (default: all)")] string filter = "all",
        CancellationToken ct = default)
    {
        if (OpenWrtForumValidation.ValidateNotificationFilter(filter) is { } e) return ValidationError(e);

        await c.EnsureAuthenticatedAsync(ct);
        var query = filter == "unread"
            ? new Dictionary<string, string?> { ["filter"] = "unread" }
            : null;
        var data = await c.GetAsync("/notifications.json", query, ct);
        var notifications = (data["notifications"] as JsonArray) ?? new JsonArray();
        var result = new JsonArray();
        foreach (var n in notifications)
        {
            var topicId = n?["topic_id"];
            var hasTopic = topicId is not null && topicId.GetValueKind() == JsonValueKind.Number;
            var topicTitle = AsString(n?["fancy_title"]);
            if (string.IsNullOrEmpty(topicTitle))
                topicTitle = AsString(n?["data"]?["topic_title"]);
            result.Add(new JsonObject
            {
                ["id"] = AsInt(n?["id"]),
                ["type"] = AsInt(n?["notification_type"]),
                ["read"] = AsBool(n?["read"]),
                ["created_at"] = AsString(n?["created_at"]),
                ["topic_id"] = hasTopic ? AsInt(topicId) : null,
                ["topic_title"] = topicTitle,
                ["excerpt"] = Trim(AsString(n?["data"]?["excerpt"]), 300),
                ["url"] = hasTopic ? $"{c.BaseUrl}/t/{AsInt(topicId)}" : null,
            });
        }
        return result.ToJsonString(_ind);
    }

    [McpServerTool(Name = "forum_mark_notifications_read")]
    [Description("Mark all notifications as read")]
    public static async Task<string> MarkNotificationsRead(DiscourseClient c, CancellationToken ct = default)
    {
        await c.PutAuthAsync("/notifications/mark-read.json", null, ct);
        return "{\"status\": \"all notifications marked read\"}";
    }
}
