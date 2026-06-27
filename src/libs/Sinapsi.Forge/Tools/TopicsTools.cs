using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>
/// Repository topic tools — list / add / remove / set the topic tags on a repo.
/// Maps to the Gitea/Forgejo + GitHub repo-topics API. Topics are additive,
/// reversible repo metadata, so the writes are Destructive=false (autonomous);
/// listing is ReadOnly. Lets an agent tag a repo (e.g. so a knowledge bridge can
/// discover it) without a hand-rolled direct API call.
/// </summary>
[McpServerToolType]
public sealed class TopicsTools
{
    [McpServerTool(Name = "list_repo_topics", ReadOnly = true)]
    [Description("List the topic tags on a repository.")]
    public static async Task<object> ListRepoTopics(IForgeClient forge, string owner, string repo,
        CancellationToken ct = default)
        => new { owner, repo, topics = await forge.ListRepoTopicsAsync(owner, repo, ct) };

    [McpServerTool(Name = "add_repo_topic", Destructive = false)]
    [Description("Add a single topic tag to a repository (idempotent). Topics must be lowercase, start with a letter/number, contain only letters/numbers/hyphens, and be <=35 chars. Returns the resulting topic list.")]
    public static async Task<object> AddRepoTopic(IForgeClient forge, string owner, string repo,
        [Description("The topic to add, e.g. \"knowledge\".")] string topic,
        CancellationToken ct = default)
        => new { owner, repo, topics = await forge.AddRepoTopicAsync(owner, repo, topic, ct) };

    [McpServerTool(Name = "remove_repo_topic", Destructive = false)]
    [Description("Remove a single topic tag from a repository (no-op if it is not present). Returns the resulting topic list.")]
    public static async Task<object> RemoveRepoTopic(IForgeClient forge, string owner, string repo,
        [Description("The topic to remove.")] string topic,
        CancellationToken ct = default)
        => new { owner, repo, topics = await forge.RemoveRepoTopicAsync(owner, repo, topic, ct) };

    [McpServerTool(Name = "set_repo_topics", Destructive = false)]
    [Description("Replace the repository's entire topic set with the given list (pass an empty list to clear all topics). Returns the resulting topic list.")]
    public static async Task<object> SetRepoTopics(IForgeClient forge, string owner, string repo,
        [Description("The complete desired set of topics; replaces whatever is currently set.")] IReadOnlyList<string> topics,
        CancellationToken ct = default)
        => new { owner, repo, topics = await forge.SetRepoTopicsAsync(owner, repo, topics, ct) };
}
