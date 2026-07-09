using System.Net;
using System.Text;
using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// L1 unit tests for the LIVE <see cref="ForgejoContentsPublisher"/> — the searchable-substrate git
/// push that makes recall work (transcripts + bundles + manifest → ste/cervello main → indexer). It
/// exercises create (POST, no sha), update-by-sha (PUT), the unchanged no-op, the absent-file skip,
/// and the LINT R7 hard floor (audio/voiceprints never published). Mock HttpClient — no network.
/// </summary>
public sealed class ForgejoContentsPublisherTests
{
    private static EnrichmentConfig Cfg() => EnrichmentConfig.From(new Dictionary<string, string?>
    {
        ["CERVELLO_FORGEJO_REPO"] = "ste/cervello",
        ["CERVELLO_FORGEJO_BASE_BRANCH"] = "main",
    });

    private static (ForgejoContentsPublisher pub, string root) NewPublisher(StubHttpMessageHandler handler)
    {
        var root = Path.Combine(Path.GetTempPath(), "cervello-pub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://forgejo.test") };
        return (new ForgejoContentsPublisher(http, new StaticBearerProvider("fp"), Cfg(), root), root);
    }

    private static void WriteLocal(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    [Fact] // a NEW transcript is created via POST (no sha); the GET first 404s (absent on main)
    public async Task Creates_a_new_transcript_via_post_when_absent_on_main()
    {
        var handler = StubHttpMessageHandler.Custom((req, _) =>
            req.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.Created)
                { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var (pub, root) = NewPublisher(handler);
        WriteLocal(root, "recordings/transcripts/rec-1.md", "# transcript body");

        var result = await pub.PublishAsync(new GitPublishRequest("rec-1", ["recordings/transcripts/rec-1.md"]));

        Assert.Contains("recordings/transcripts/rec-1.md", result.Pushed);
        Assert.False(result.WasNoOp);
        // GET (404) then POST (create) — the create carries no sha.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.DoesNotContain("\"sha\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.All(handler.Requests, r => Assert.Equal("fp", r.Bearer)); // agent-free bearer everywhere
    }

    [Fact] // an EXISTING, CHANGED manifest is updated via PUT carrying the current sha
    public async Task Updates_an_existing_changed_file_via_put_with_sha()
    {
        var existingB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("[]\n")); // old manifest content
        var handler = StubHttpMessageHandler.Custom((req, _) =>
            req.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($$"""{ "sha": "abc123", "content": "{{existingB64}}", "encoding": "base64" }""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var (pub, root) = NewPublisher(handler);
        WriteLocal(root, "recordings/manifest.yaml", "- id: rec-1\n"); // CHANGED vs "[]\n"

        var result = await pub.PublishAsync(new GitPublishRequest("rec-1", ["recordings/manifest.yaml"]));

        Assert.Contains("recordings/manifest.yaml", result.Pushed);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Contains("\"sha\":\"abc123\"", handler.Requests[1].Body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact] // an UNCHANGED file (same bytes as git) is a NO-OP — no create/update, no empty commit
    public async Task Unchanged_file_is_a_no_op()
    {
        var same = "same bytes";
        var sameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(same));
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK,
            $$"""{ "sha": "s1", "content": "{{sameB64}}", "encoding": "base64" }""");
        var (pub, root) = NewPublisher(handler);
        WriteLocal(root, "recordings/transcripts/rec-1.md", same);

        var result = await pub.PublishAsync(new GitPublishRequest("rec-1", ["recordings/transcripts/rec-1.md"]));

        Assert.Empty(result.Pushed);
        Assert.Contains("recordings/transcripts/rec-1.md", result.Skipped);
        Assert.Single(handler.Requests);                         // only the GET; no write
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact] // a path the run didn't produce (absent on-CT) is SKIPPED, never fabricated
    public async Task Absent_local_file_is_skipped_never_fabricated()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.OK); // should never be hit
        var (pub, _) = NewPublisher(handler);

        var result = await pub.PublishAsync(new GitPublishRequest("rec-1", ["inbox/rec-1/bundle.md"]));

        Assert.Empty(result.Pushed);
        Assert.Contains("inbox/rec-1/bundle.md", result.Skipped);
        Assert.Empty(handler.Requests);                          // no forgejo call for an absent file
    }

    [Theory] // LINT R7: audio + voiceprints NEVER enter git — the publisher REFUSES such a path
    [InlineData("recordings/audio/rec-1.m4a")]
    [InlineData("recordings/voiceprints/guilhem.vec")]
    public async Task Refuses_to_publish_a_never_git_path(string forbidden)
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.OK);
        var (pub, root) = NewPublisher(handler);
        WriteLocal(root, forbidden, "SHOULD NEVER BE PUSHED");

        await Assert.ThrowsAsync<GitPublishException>(() =>
            pub.PublishAsync(new GitPublishRequest("rec-1", [forbidden])));
        Assert.Empty(handler.Requests);                          // refused before any forgejo call
    }
}
