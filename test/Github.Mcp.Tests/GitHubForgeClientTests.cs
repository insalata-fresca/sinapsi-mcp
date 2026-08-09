using System.Net;
using System.Text;
using System.Text.Json;
using Github.Mcp.Forge;
using Sinapsi.Forge;
using Sinapsi.Forge.Model;
using Xunit;

namespace Github.Mcp.Tests;

/// <summary>
/// Verifies the GitHub adapter's byte-safe commit_files via the Git Data API: ref → base
/// commit → blob(s) (exact base64) → tree → commit → ref move. Uses a scripted fake transport.
/// </summary>
public sealed class GitHubForgeClientTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Body)> Calls = new();
        private readonly Queue<string> _responses;
        public ScriptedHandler(IEnumerable<string> responses) => _responses = new(responses);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            var json = _responses.Count > 0 ? _responses.Dequeue() : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task CommitFiles_via_GitData_sends_exact_base64_blobs_and_moves_ref()
    {
        byte[] raw = { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0xFE, 0x0A };
        string b64 = Convert.ToBase64String(raw);

        var handler = new ScriptedHandler(new[]
        {
            """{"object":{"sha":"BASECOMMIT"}}""",          // GET ref
            """{"tree":{"sha":"BASETREE"}}""",               // GET base commit
            """{"sha":"BLOB1"}""",                            // POST blob (create)
            """{"sha":"NEWTREE"}""",                          // POST tree
            """{"sha":"NEWCOMMIT","html_url":"https://github.com/o/r/commit/NEWCOMMIT"}""", // POST commit
            """{"ref":"refs/heads/main"}""",                 // PATCH ref
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubForgeClient(http);

        var files = new List<ForgeFileChange>
        {
            new("docs/a.pdf", "create", b64, null, null),
            new("old.txt", "delete", null, null, null),
        };
        var res = await client.CommitFilesAsync("o", "r", "main", null, "msg", files);

        // The blob POST carries the exact base64 + encoding=base64.
        var blobCall = handler.Calls.Single(c => c.Path.EndsWith("/git/blobs"));
        using var blobDoc = JsonDocument.Parse(blobCall.Body!);
        Assert.Equal(b64, blobDoc.RootElement.GetProperty("content").GetString());
        Assert.Equal("base64", blobDoc.RootElement.GetProperty("encoding").GetString());
        Assert.Equal(raw, Convert.FromBase64String(blobDoc.RootElement.GetProperty("content").GetString()!));

        // The tree includes the delete as sha:null and the create as the blob sha.
        var treeCall = handler.Calls.Single(c => c.Path.EndsWith("/git/trees"));
        using var treeDoc = JsonDocument.Parse(treeCall.Body!);
        var entries = treeDoc.RootElement.GetProperty("tree");
        Assert.Equal("BASETREE", treeDoc.RootElement.GetProperty("base_tree").GetString());
        var create = entries.EnumerateArray().Single(e => e.GetProperty("path").GetString() == "docs/a.pdf");
        Assert.Equal("BLOB1", create.GetProperty("sha").GetString());
        var del = entries.EnumerateArray().Single(e => e.GetProperty("path").GetString() == "old.txt");
        Assert.Equal(JsonValueKind.Null, del.GetProperty("sha").ValueKind);

        // The commit parents the base; the ref is moved to the new commit.
        var commitCall = handler.Calls.Single(c => c.Path.EndsWith("/git/commits") && c.Method == "POST");
        using var commitDoc = JsonDocument.Parse(commitCall.Body!);
        Assert.Equal("NEWTREE", commitDoc.RootElement.GetProperty("tree").GetString());
        Assert.Equal("BASECOMMIT", commitDoc.RootElement.GetProperty("parents")[0].GetString());

        var patchCall = handler.Calls.Single(c => c.Method == "PATCH" && c.Path.Contains("/git/refs/heads/main"));
        using var patchDoc = JsonDocument.Parse(patchCall.Body!);
        Assert.Equal("NEWCOMMIT", patchDoc.RootElement.GetProperty("sha").GetString());

        Assert.Equal("NEWCOMMIT", res.CommitSha);
        Assert.Equal("main", res.Branch);
    }

    [Fact]
    public async Task PullReview_event_APPROVED_is_normalised_to_APPROVE()
    {
        var handler = new ScriptedHandler(new[] { """{"id":1,"state":"APPROVED"}""" });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubForgeClient(http);

        await client.CreatePullReviewAsync("o", "r", 5, "APPROVED", "lgtm");

        using var doc = JsonDocument.Parse(handler.Calls.Single().Body!);
        Assert.Equal("APPROVE", doc.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public async Task TimeTracking_throws_NotSupported_on_GitHub()
    {
        var http = new HttpClient(new ScriptedHandler(Array.Empty<string>())) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubForgeClient(http);
        await Assert.ThrowsAsync<ForgeNotSupportedException>(() => client.AddIssueTimeAsync("o", "r", 1, 60));
    }

    [Fact]
    public async Task DispatchWorkflow_posts_ref_and_inputs_to_dispatches_endpoint()
    {
        var handler = new ScriptedHandler(Array.Empty<string>());      // 204 → "{}" fallback, body ignored
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubForgeClient(http);

        var res = await client.DispatchWorkflowAsync("o", "r", "build.yml", "main",
            new Dictionary<string, string> { ["env"] = "prod" });

        var call = handler.Calls.Single();
        Assert.Equal("POST", call.Method);
        Assert.EndsWith("/repos/o/r/actions/workflows/build.yml/dispatches", call.Path);
        using var doc = JsonDocument.Parse(call.Body!);
        Assert.Equal("main", doc.RootElement.GetProperty("ref").GetString());
        Assert.Equal("prod", doc.RootElement.GetProperty("inputs").GetProperty("env").GetString());
        Assert.True(res.Dispatched);
    }

    [Fact]
    public async Task ListWorkflowRuns_filtered_hits_workflow_runs_path_and_maps_conclusion()
    {
        const string json = """
        {"total_count":1,"workflow_runs":[
          {"id":99,"run_number":12,"name":"build","display_title":"build docs",
           "status":"completed","conclusion":"success","event":"workflow_dispatch",
           "head_sha":"cafe","head_branch":"main","html_url":"https://github.com/o/r/actions/runs/99",
           "created_at":"2026-06-02T10:00:00Z","updated_at":"2026-06-02T10:05:00Z"}]}
        """;
        var handler = new ScriptedHandler(new[] { json });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubForgeClient(http);

        var runs = await client.ListWorkflowRunsAsync("o", "r", "build.yml", limit: 5);

        var call = handler.Calls.Single();
        Assert.Equal("GET", call.Method);
        Assert.EndsWith("/repos/o/r/actions/workflows/build.yml/runs", call.Path);
        var run = Assert.Single(runs);
        Assert.Equal(99, run.Id);
        Assert.Equal(12, run.RunNumber);
        Assert.Equal("completed", run.Status);
        Assert.Equal("success", run.Conclusion);
        Assert.Equal("cafe", run.HeadSha);
    }

    // ── edit_repo: auto-delete branch on merge ─────────────────────────────────
    // GitHub spells this `delete_branch_on_merge`; the Forgejo/Gitea name
    // `default_delete_branch_after_merge` must NEVER appear in a GitHub payload.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditRepo_maps_the_flag_to_the_GitHub_field_name(bool value)
    {
        var handler = new ScriptedHandler(new[] { """{"full_name":"o/r","private":false}""" });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubForgeClient(http);

        await client.EditRepoAsync("o", "r", new EditRepoRequest(DefaultDeleteBranchAfterMerge: value));

        var patch = handler.Calls.Single(c => c.Method == "PATCH");
        Assert.Equal("/repos/o/r", patch.Path);
        using var doc = JsonDocument.Parse(patch.Body!);
        Assert.Equal(value, doc.RootElement.GetProperty("delete_branch_on_merge").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("default_delete_branch_after_merge", out _));
    }

    [Fact]
    public async Task EditRepo_omits_delete_branch_on_merge_when_null()
    {
        // An unrelated edit must not carry the flag — sending it would reset the repo setting.
        var handler = new ScriptedHandler(new[] { """{"full_name":"o/r","private":false}""" });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubForgeClient(http);

        await client.EditRepoAsync("o", "r", new EditRepoRequest(Description: "unrelated edit"));

        var patch = handler.Calls.Single(c => c.Method == "PATCH");
        using var doc = JsonDocument.Parse(patch.Body!);
        Assert.Equal("unrelated edit", doc.RootElement.GetProperty("description").GetString());
        Assert.False(doc.RootElement.TryGetProperty("delete_branch_on_merge", out _));
    }

    [Fact]
    public async Task SetRepoTopics_puts_names_payload_and_returns_them()
    {
        var handler = new ScriptedHandler(new[] { """{"names":["alpha","beta"]}""" });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubForgeClient(http);

        var topics = await client.SetRepoTopicsAsync("o", "r", new[] { "alpha", "beta" });

        var put = handler.Calls.Single(c => c.Method == "PUT");
        Assert.EndsWith("/repos/o/r/topics", put.Path);
        using var doc = JsonDocument.Parse(put.Body!);
        Assert.Equal(2, doc.RootElement.GetProperty("names").GetArrayLength());
        Assert.Equal(new[] { "alpha", "beta" }, topics);
    }

    [Fact]
    public async Task AddRepoTopic_reads_then_replaces_with_the_appended_topic()
    {
        // GitHub has no single-topic endpoint: GET existing names, then PUT the union.
        var handler = new ScriptedHandler(new[]
        {
            """{"names":["alpha"]}""",          // GET existing
            """{"names":["alpha","beta"]}""",   // PUT replace → merged result
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubForgeClient(http);

        var topics = await client.AddRepoTopicAsync("o", "r", "beta");

        Assert.Equal("GET", handler.Calls[0].Method);
        Assert.Equal("PUT", handler.Calls[1].Method);
        using var doc = JsonDocument.Parse(handler.Calls[1].Body!);
        var names = doc.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
        Assert.Equal(new[] { "alpha", "beta" }, topics);
    }
}
