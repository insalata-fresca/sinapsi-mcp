using System.Net;
using System.Text;
using System.Text.Json;
using Github.Mcp.Forge;
using Sinapsi.Forge;
using Sinapsi.Forge.Tools;
using Xunit;

namespace Github.Mcp.Tests;

/// <summary>
/// The repository "About" metadata surface on the GitHub adapter: the homepage field on
/// <c>edit_repo</c>, and the topic tools.
///
/// <para>
/// Both were reachable only by hand-rolled direct API calls before: <c>homepage</c> was absent
/// from <see cref="EditRepoRequest"/> entirely, and the topic tools — though fully implemented
/// on <see cref="GitHubForgeClient"/> — were never registered on the github-mcp host. A repo
/// whose About box is blank renders GitHub's "Contribute to …" boilerplate everywhere it is
/// linked, so this is user-visible metadata, not cosmetics.
/// </para>
///
/// <para>
/// The add/remove topic paths are the load-bearing ones: GitHub exposes no per-topic endpoint,
/// so the adapter does a read-modify-write over the replace-all <c>PUT …/topics</c>. A bug there
/// does not fail loudly — it silently discards every topic it forgot to carry forward.
/// </para>
/// </summary>
public sealed class GitHubRepoMetadataTests
{
    private sealed class ScriptedHandler(IEnumerable<string> responses) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Body)> Calls = new();
        private readonly Queue<string> _responses = new(responses);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            var json = _responses.Count > 0 ? _responses.Dequeue() : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static (GitHubForgeClient client, ScriptedHandler handler) Make(params string[] responses)
    {
        var handler = new ScriptedHandler(responses.Length > 0 ? responses : ["""{"full_name":"o/r"}"""]);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubForgeClient(http), handler);
    }

    // ── edit_repo → homepage ─────────────────────────────────────────────────

    [Fact]
    public async Task EditRepo_threads_homepage_to_the_github_homepage_field()
    {
        var (client, handler) = Make();

        await RepoTools.EditRepo(client, "o", "r", homepage: "https://example.test/x");

        var call = handler.Calls.Single();
        Assert.Equal("PATCH", call.Method);
        using var doc = JsonDocument.Parse(call.Body!);
        // GitHub's field is `homepage` — NOT the Forgejo/Gitea `website`.
        Assert.Equal("https://example.test/x", doc.RootElement.GetProperty("homepage").GetString());
        Assert.False(doc.RootElement.TryGetProperty("website", out _));
    }

    [Fact]
    public async Task EditRepo_omits_homepage_when_not_passed_so_an_unrelated_edit_cannot_clear_it()
    {
        var (client, handler) = Make();

        await RepoTools.EditRepo(client, "o", "r", description: "unrelated edit");

        using var doc = JsonDocument.Parse(handler.Calls.Single().Body!);
        Assert.Equal("unrelated edit", doc.RootElement.GetProperty("description").GetString());
        Assert.False(doc.RootElement.TryGetProperty("homepage", out _));
    }

    [Fact]
    public async Task EditRepo_sends_an_explicit_empty_homepage_so_it_can_be_cleared()
    {
        var (client, handler) = Make();

        // "" is meaningfully different from null: null is pruned (leave alone), "" clears.
        await RepoTools.EditRepo(client, "o", "r", homepage: "");

        using var doc = JsonDocument.Parse(handler.Calls.Single().Body!);
        Assert.True(doc.RootElement.TryGetProperty("homepage", out var hp));
        Assert.Equal("", hp.GetString());
    }

    [Fact]
    public async Task GetRepo_surfaces_the_homepage_so_a_read_can_confirm_what_a_write_set()
    {
        var (client, _) = Make("""{"full_name":"o/r","homepage":"https://example.test/x"}""");

        var repo = await client.GetRepoAsync("o", "r");

        Assert.Equal("https://example.test/x", repo.Homepage);
    }

    // ── topics ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetRepoTopics_replaces_the_whole_set_in_one_put()
    {
        var (client, handler) = Make("""{"names":["a","b"]}""");

        var result = await client.SetRepoTopicsAsync("o", "r", ["a", "b"]);

        var call = handler.Calls.Single();
        Assert.Equal("PUT", call.Method);
        Assert.Equal("/repos/o/r/topics", call.Path);
        using var doc = JsonDocument.Parse(call.Body!);
        Assert.Equal(["a", "b"], doc.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(["a", "b"], result);
    }

    [Fact]
    public async Task AddRepoTopic_carries_the_existing_topics_forward_rather_than_replacing_them()
    {
        // GitHub has no per-topic endpoint, so add == read-modify-write. If the read result
        // were dropped, adding one topic would wipe every other topic on the repo.
        var (client, handler) = Make(
            """{"names":["existing-one","existing-two"]}""",   // GET current
            """{"names":["existing-one","existing-two","added"]}""");  // PUT replace

        var result = await client.AddRepoTopicAsync("o", "r", "added");

        Assert.Equal("GET", handler.Calls[0].Method);
        Assert.Equal("PUT", handler.Calls[1].Method);
        using var doc = JsonDocument.Parse(handler.Calls[1].Body!);
        var sent = doc.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["existing-one", "existing-two", "added"], sent);
        Assert.Equal(["existing-one", "existing-two", "added"], result);
    }

    [Fact]
    public async Task AddRepoTopic_is_idempotent_and_case_insensitive()
    {
        var (client, handler) = Make(
            """{"names":["already"]}""",
            """{"names":["already"]}""");

        await client.AddRepoTopicAsync("o", "r", "ALREADY");

        using var doc = JsonDocument.Parse(handler.Calls[1].Body!);
        var sent = doc.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["already"], sent);
    }

    [Fact]
    public async Task RemoveRepoTopic_drops_only_the_named_topic()
    {
        var (client, handler) = Make(
            """{"names":["keep-one","drop-me","keep-two"]}""",
            """{"names":["keep-one","keep-two"]}""");

        await client.RemoveRepoTopicAsync("o", "r", "drop-me");

        using var doc = JsonDocument.Parse(handler.Calls[1].Body!);
        var sent = doc.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["keep-one", "keep-two"], sent);
    }
}
