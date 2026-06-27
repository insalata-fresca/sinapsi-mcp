using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Sinapsi.Indexer;

/// <summary>
/// The MCP WRITE surface for durable learnings: the tool performs the action BY
/// emitting an event. This is the canonical ingress for "publish a learning" —
/// a uniform MCP seam over the event bus. Persistence is decoupled: the emitted
/// <c>{LEARN_SUBJECT_PREFIX}.{scope}.published</c> event is consumed by a
/// downstream materializer that writes the learnings repo, which this indexer
/// then re-scans + serves.
/// </summary>
[McpServerToolType]
public sealed partial class LearnTools
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex TokenRx();

    [McpServerTool(Name = "publish_learning")]
    [Description(
        "Persist a durable cross-project learning the canonical way: emits a " +
        "learning-published event on NATS → a downstream materializer writes it to " +
        "the learnings repo → indexed + MCP-served. Use for hard facts / patterns / " +
        ">5-min gotchas worth keeping — NOT opinions or per-task narrative. Dedupe first " +
        "(search_index / get_learning); reuse the SAME slug to refresh an existing entry.")]
    public static async Task<object> PublishLearning(
        LearnPublisher publisher,
        [Description("Short kebab-case slug = the entry id (reuse to refresh). e.g. 'debian13-lxc-no-sudo'.")] string slug,
        [Description("One-line summary title.")] string title,
        [Description("Full markdown body (NO frontmatter; use '## Claim' / '## How we know'). NEVER include secrets.")] string body,
        [Description("Scope bucket: 'global' or a project slug (e.g. 'docs').")] string scope = "global",
        [Description("Tags for the entry.")] string[]? tags = null,
        [Description("One-line session context, e.g. 'session 2026-01-01 — investigation'.")] string? session_context = null,
        CancellationToken cancellationToken = default)
    {
        slug = (slug ?? "").Trim();
        scope = string.IsNullOrWhiteSpace(scope) ? "global" : scope.Trim();
        if (!TokenRx().IsMatch(slug))
            return new { error = "slug must be kebab-case [a-z0-9-] (it is the entry id + NATS subject token)" };
        if (!TokenRx().IsMatch(scope))
            return new { error = "scope must be a NATS-safe token [a-z0-9-] — e.g. 'global' or a project slug" };
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
            return new { error = "title and body are required" };

        var tagArr = new JsonArray();
        foreach (var t in tags ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(t)) tagArr.Add(t.Trim());

        var data = new JsonObject
        {
            ["slug"] = slug,
            ["title"] = title.Trim(),
            ["scope"] = scope,
            ["tags"] = tagArr,
            ["body"] = body,
        };
        if (!string.IsNullOrWhiteSpace(session_context))
            data["session_context"] = session_context.Trim();

        await publisher.PublishLearningAsync(slug, scope, data, cancellationToken);
        return new { published = true, subject = publisher.SubjectFor(scope), slug };
    }
}
