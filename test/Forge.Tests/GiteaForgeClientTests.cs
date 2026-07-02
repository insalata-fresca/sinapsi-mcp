using System.Net;
using System.Text;
using System.Text.Json;
using Sinapsi.Forge;
using Sinapsi.Forge.Gitea;
using Sinapsi.Forge.Model;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Verifies the byte-safety + request shaping of <see cref="GiteaForgeClient"/> against a
/// fake transport — no live forge. The core promise: bytes in == bytes out, atomic
/// multi-file, correct create-vs-update verb.
/// </summary>
public sealed class GiteaForgeClientTests
{
    private sealed class CapturingHandler(HttpStatusCode status, string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
        }
    }

    private static (GiteaForgeClient client, CapturingHandler handler) Make(HttpStatusCode status, string json)
    {
        var handler = new CapturingHandler(status, json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://forge.example/api/v1/") };
        return (new GiteaForgeClient(http), handler);
    }

    [Fact]
    public async Task CommitFiles_sends_exact_base64_atomically()
    {
        // A real binary payload (non-UTF-8 bytes) → base64.
        byte[] raw = { 0x25, 0x50, 0x44, 0x46, 0x00, 0x01, 0xFF, 0xFE, 0x0A };
        string b64 = Convert.ToBase64String(raw);
        var (client, handler) = Make(HttpStatusCode.Created, """{"commit":{"sha":"abc123","html_url":"https://forge.example/x/y/commit/abc123"}}""");

        var files = new List<ForgeFileChange>
        {
            new("documents/a.pdf", "create", b64, null, null),
            new("README.md", "update", Convert.ToBase64String(Encoding.UTF8.GetBytes("# hi\n")), "oldsha", null),
        };
        var res = await client.CommitFilesAsync("ste", "demo-repo", "main", null, "add docs", files);

        // One request, correct endpoint + verb.
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.EndsWith("/repos/ste/demo-repo/contents", handler.Request!.RequestUri!.AbsolutePath);

        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        Assert.Equal("main", root.GetProperty("branch").GetString());
        Assert.Equal("add docs", root.GetProperty("message").GetString());
        var sent = root.GetProperty("files");
        Assert.Equal(2, sent.GetArrayLength());

        // Byte-perfect: the base64 in the request equals what we passed; decoding yields the original bytes.
        var first = sent[0];
        Assert.Equal("create", first.GetProperty("operation").GetString());
        Assert.Equal("documents/a.pdf", first.GetProperty("path").GetString());
        Assert.Equal(b64, first.GetProperty("content").GetString());
        Assert.Equal(raw, Convert.FromBase64String(first.GetProperty("content").GetString()!));

        // update carries the sha; create does not.
        Assert.Equal("oldsha", sent[1].GetProperty("sha").GetString());
        Assert.False(first.TryGetProperty("sha", out _)); // pruned null

        Assert.Equal("abc123", res.CommitSha);
        Assert.Equal("main", res.Branch);
    }

    [Fact]
    public async Task CreateOrUpdate_uses_POST_to_create_and_PUT_to_update()
    {
        var (create, hCreate) = Make(HttpStatusCode.Created, """{"commit":{"sha":"c1"}}""");
        await create.CreateOrUpdateFileAsync("o", "r", "f.txt", "Zm9v", "m", "main", sha: null);
        Assert.Equal(HttpMethod.Post, hCreate.Request!.Method);

        var (update, hUpdate) = Make(HttpStatusCode.OK, """{"commit":{"sha":"c2"}}""");
        await update.CreateOrUpdateFileAsync("o", "r", "f.txt", "YmFy", "m", "main", sha: "deadbeef");
        Assert.Equal(HttpMethod.Put, hUpdate.Request!.Method);
        using var doc = JsonDocument.Parse(hUpdate.Body!);
        Assert.Equal("deadbeef", doc.RootElement.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task GetFileBinary_round_trips_raw_bytes_to_base64()
    {
        byte[] raw = { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x10 };
        var handler = new RawHandler(raw, "application/pdf");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://forge.example/api/v1/") };
        var client = new GiteaForgeClient(http);

        var bin = await client.GetFileBinaryAsync("o", "r", "x.pdf", "main");

        Assert.Equal(raw.Length, bin.Size);
        Assert.Equal("application/pdf", bin.MimeTypeGuess);
        Assert.Equal(raw, Convert.FromBase64String(bin.ContentBase64));
        Assert.Contains("/media/x.pdf", handler.Path);
    }

    private sealed class RawHandler(byte[] bytes, string mime) : HttpMessageHandler
    {
        public string Path = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task ApiError_throws_ForgeApiException_with_status()
    {
        var (client, _) = Make(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        var ex = await Assert.ThrowsAsync<ForgeApiException>(() => client.GetRepoAsync("o", "missing"));
        Assert.Equal(404, ex.Status);
    }

    // ── merge: confirm-after-merge + surface rejection + bounded transient retry ──────────

    // Replays a scripted list of responses in order; records every request it saw.
    private sealed class SequenceHandler(params (HttpStatusCode status, string json)[] responses) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string?> Bodies = new();
        private int _i;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            var (status, json) = responses[Math.Min(_i, responses.Length - 1)];
            _i++;
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static GiteaForgeClient MakeSeq(SequenceHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://forge.example/api/v1/") });

    [Fact]
    public async Task Merge_confirmed_when_GET_shows_merged_true()
    {
        // POST merge → 200; GET PR → merged:true.
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, """{"number":7,"title":"t","state":"closed","merged":true}"""));
        var res = await MakeSeq(handler).MergePullRequestAsync("o", "r", 7, "merge", null, null);

        Assert.True(res.Merged);
        Assert.Null(res.Message);
        Assert.Equal(7, res.Number);
        // POST then a confirming GET.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.EndsWith("/pulls/7/merge", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.EndsWith("/pulls/7", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Merge_no_op_reported_as_not_merged_with_reason()
    {
        // POST merge → 200 but the PR raced and is still open → merged:false + reason.
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, """{"number":8,"title":"t","state":"open","merged":false}"""));
        var res = await MakeSeq(handler).MergePullRequestAsync("o", "r", 8, "merge", null, null);

        Assert.False(res.Merged);
        Assert.NotNull(res.Message);
        Assert.Contains("still open", res.Message);
    }

    [Fact]
    public async Task Merge_rejected_4xx_throws_with_status_and_body()
    {
        // POST merge → 405 (branch protection / required checks) — surfaced, never retried.
        var handler = new SequenceHandler(
            (HttpStatusCode.MethodNotAllowed, """{"message":"Branch protection: required checks failing"}"""));
        var ex = await Assert.ThrowsAsync<ForgeApiException>(
            () => MakeSeq(handler).MergePullRequestAsync("o", "r", 9, "merge", null, null));

        Assert.Equal(405, ex.Status);
        Assert.Contains("405", ex.Message);
        Assert.Contains("Branch protection", ex.Message);
        // 4xx is NOT retried, and no confirming GET fires once it's rejected.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Merge_4xx_is_not_retried()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.Conflict, """{"message":"merge conflict"}"""));
        await Assert.ThrowsAsync<ForgeApiException>(
            () => MakeSeq(handler).MergePullRequestAsync("o", "r", 10, "merge", null, null));

        Assert.Single(handler.Requests); // exactly one POST, no retry.
    }

    [Fact]
    public async Task Merge_5xx_is_retried_exactly_once_then_succeeds()
    {
        // First POST → 502 (transient); retried POST → 200; then the confirming GET.
        var handler = new SequenceHandler(
            (HttpStatusCode.BadGateway, """{"message":"bad gateway"}"""),
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, """{"number":11,"title":"t","state":"closed","merged":true}"""));
        var res = await MakeSeq(handler).MergePullRequestAsync("o", "r", 11, "merge", null, null);

        Assert.True(res.Merged);
        // POST(502) + POST(200) + GET(confirm) = 3 requests.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[2].Method);
    }

    [Fact]
    public async Task Merge_5xx_retried_only_once_then_gives_up()
    {
        // Two consecutive 5xx — one retry only, then the second 5xx is surfaced.
        var handler = new SequenceHandler(
            (HttpStatusCode.BadGateway, """{"message":"bad gateway"}"""),
            (HttpStatusCode.ServiceUnavailable, """{"message":"unavailable"}"""));
        var ex = await Assert.ThrowsAsync<ForgeApiException>(
            () => MakeSeq(handler).MergePullRequestAsync("o", "r", 12, "merge", null, null));

        Assert.Equal(503, ex.Status);
        Assert.Equal(2, handler.Requests.Count); // original + exactly one retry, no GET.
    }

    [Fact]
    public async Task DispatchWorkflow_posts_ref_and_inputs_to_the_dispatch_endpoint()
    {
        var (client, handler) = Make(HttpStatusCode.NoContent, "");
        var res = await client.DispatchWorkflowAsync("ste", "demo-repo", "build.yml", "main",
            new Dictionary<string, string> { ["tag"] = "v2" });

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.EndsWith("/repos/ste/demo-repo/actions/workflows/build.yml/dispatches",
            handler.Request!.RequestUri!.AbsolutePath);

        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        Assert.Equal("main", root.GetProperty("ref").GetString());
        Assert.Equal("v2", root.GetProperty("inputs").GetProperty("tag").GetString());

        Assert.True(res.Dispatched);
        Assert.Equal("build.yml", res.Workflow);
        Assert.Equal("main", res.Ref);
    }

    [Fact]
    public async Task DispatchWorkflow_omits_inputs_when_none_given()
    {
        var (client, handler) = Make(HttpStatusCode.NoContent, "");
        await client.DispatchWorkflowAsync("ste", "demo-repo", "build.yml", "main", inputs: null);

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("main", doc.RootElement.GetProperty("ref").GetString());
        Assert.False(doc.RootElement.TryGetProperty("inputs", out _)); // pruned null
    }

    [Fact]
    public async Task ListWorkflowRuns_unwraps_workflow_runs_and_maps_fields()
    {
        const string json = """
        {"total_count":1,"workflow_runs":[
          {"id":42,"index_in_repo":7,"workflow_id":"build.yml","title":"build docs",
           "status":"success","trigger_event":"workflow_dispatch","commit_sha":"deadbeef",
           "prettyref":"main","html_url":"https://forge.example/ste/demo-repo/actions/runs/7",
           "created":"2026-06-02T10:00:00Z","updated":"2026-06-02T10:05:00Z"}]}
        """;
        var (client, handler) = Make(HttpStatusCode.OK, json);
        var runs = await client.ListWorkflowRunsAsync("ste", "demo-repo", "build.yml", limit: 10);

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.EndsWith("/repos/ste/demo-repo/actions/runs", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Contains("workflow_id=build.yml", handler.Request!.RequestUri!.Query);
        Assert.Contains("limit=10", handler.Request!.RequestUri!.Query);

        var run = Assert.Single(runs);
        Assert.Equal(42, run.Id);
        Assert.Equal(7, run.RunNumber);
        Assert.Equal("build.yml", run.WorkflowId);
        Assert.Equal("success", run.Status);
        Assert.Equal("workflow_dispatch", run.Event);
        Assert.Equal("deadbeef", run.HeadSha);
        Assert.Equal("main", run.HeadBranch);
    }

    [Fact]
    public async Task ListWorkflowRuns_without_filter_omits_workflow_id()
    {
        var (client, handler) = Make(HttpStatusCode.OK, """{"total_count":0,"workflow_runs":[]}""");
        var runs = await client.ListWorkflowRunsAsync("ste", "demo-repo", workflow: null);

        Assert.Empty(runs);
        Assert.DoesNotContain("workflow_id", handler.Request!.RequestUri!.Query);
    }

    // ── Repository topics ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListRepoTopics_unwraps_the_topics_array()
    {
        var (client, handler) = Make(HttpStatusCode.OK, """{"topics":["alpha","beta"]}""");
        var topics = await client.ListRepoTopicsAsync("ste", "demo-repo");

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.EndsWith("/repos/ste/demo-repo/topics", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal(new[] { "alpha", "beta" }, topics);
    }

    [Fact]
    public async Task ListRepoTopics_tolerates_null_topics()
    {
        var (client, _) = Make(HttpStatusCode.OK, """{"topics":null}""");
        Assert.Empty(await client.ListRepoTopicsAsync("o", "r"));
    }

    [Fact]
    public async Task AddRepoTopic_puts_the_single_topic_then_relists()
    {
        // PUT /topics/{topic} → 204, then a GET /topics to return the live set.
        var handler = new SequenceHandler(
            (HttpStatusCode.NoContent, ""),
            (HttpStatusCode.OK, """{"topics":["knowledge"]}"""));
        var topics = await MakeSeq(handler).AddRepoTopicAsync("ste", "demo-repo", "knowledge");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.EndsWith("/repos/ste/demo-repo/topics/knowledge", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal(new[] { "knowledge" }, topics);
    }

    [Fact]
    public async Task RemoveRepoTopic_deletes_the_single_topic_then_relists()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.NoContent, ""),
            (HttpStatusCode.OK, """{"topics":[]}"""));
        var topics = await MakeSeq(handler).RemoveRepoTopicAsync("ste", "demo-repo", "knowledge");

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.EndsWith("/repos/ste/demo-repo/topics/knowledge", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Empty(topics);
    }

    [Fact]
    public async Task SetRepoTopics_puts_the_topics_body_then_relists()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.NoContent, ""),
            (HttpStatusCode.OK, """{"topics":["a","b"]}"""));
        var topics = await MakeSeq(handler).SetRepoTopicsAsync("o", "r", new[] { "a", "b" });

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.EndsWith("/repos/o/r/topics", handler.Requests[0].RequestUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(handler.Bodies[0]!);
        var arr = doc.RootElement.GetProperty("topics");
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("a", arr[0].GetString());
        Assert.Equal(new[] { "a", "b" }, topics);
    }

    // ── Codeberg: the same Gitea adapter, just a different base address ──────────

    [Fact]
    public async Task GiteaClient_drives_a_codeberg_base_address_identically()
    {
        // Codeberg is a Gitea instance, so the same GiteaForgeClient serves it — only the
        // configured base address differs. The request path + verb are byte-for-byte the
        // same as the Forgejo case; nothing is forked per forge.
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"id":1,"full_name":"o/r","private":false}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://codeberg.example.org/api/v1/") };
        var client = new GiteaForgeClient(http);

        var repo = await client.GetRepoAsync("o", "r");

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("codeberg.example.org", handler.Request!.RequestUri!.Host);
        Assert.EndsWith("/api/v1/repos/o/r", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("o/r", repo.FullName);
    }
}
