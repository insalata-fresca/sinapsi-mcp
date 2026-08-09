using System.Net;
using System.Text.Json;
using Sinapsi.Forge.Gitea;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Forgejo/Gitea deletes the head branch ONLY if the merge CALL asks for it. The repo-level
/// `default_delete_branch_after_merge` merely pre-ticks the UI checkbox and does not govern API
/// merges — verified live 2026-08-07: with that setting enabled, an API merge still left its
/// branch behind. Every merge in this fleet is an API merge, so these assertions are the thing
/// standing between us and another 460-branch accumulation.
/// </summary>
public class MergeDeletesBranchTests
{
    private static (GiteaForgeClient client, List<string> bodies) Harness()
    {
        var bodies = new List<string>();
        var handler = new CapturingHandler(bodies);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://forge.test/api/v1/") };
        return (new GiteaForgeClient(http), bodies);
    }

    // NOTE ON LAYERS. The ADAPTER takes bool? and defaults to null (omit -> let the server
    // decide) so a library caller keeps a genuine tri-state. The TOOL defaults to TRUE, because
    // that is the agent-facing surface where "I forgot the flag" must not mean "leave a branch".
    // This test drives the TOOL for exactly that reason — asserting the adapter's default here
    // would test the wrong layer and pass while agents still littered branches.
    [Fact]
    public async Task The_TOOL_defaults_to_deleting_the_head_branch()
    {
        var (c, bodies) = Harness();
        await Sinapsi.Forge.Tools.PullRequestTools.MergePullRequest(c, "o", "r", 1);
        var merge = bodies.First(b => b.Contains("\"Do\""));
        using var doc = JsonDocument.Parse(merge);
        Assert.True(doc.RootElement.TryGetProperty("delete_branch_after_merge", out var v),
            "the merge_pull_request TOOL must ask for branch deletion by default");
        Assert.True(v.GetBoolean());
    }

    [Fact]
    public async Task Merge_honours_an_explicit_false()
    {
        var (c, bodies) = Harness();
        await c.MergePullRequestAsync("o", "r", 1, "merge", null, null, deleteBranchAfterMerge: false);
        var merge = bodies.First(b => b.Contains("\"Do\""));
        using var doc = JsonDocument.Parse(merge);
        Assert.False(doc.RootElement.GetProperty("delete_branch_after_merge").GetBoolean());
    }

    [Fact]
    public async Task Merge_omits_the_flag_when_null_so_the_server_default_applies()
    {
        var (c, bodies) = Harness();
        await c.MergePullRequestAsync("o", "r", 1, "merge", null, null, deleteBranchAfterMerge: null);
        var merge = bodies.First(b => b.Contains("\"Do\""));
        using var doc = JsonDocument.Parse(merge);
        Assert.False(doc.RootElement.TryGetProperty("delete_branch_after_merge", out _));
    }

    private sealed class CapturingHandler(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Content is not null) bodies.Add(await req.Content.ReadAsStringAsync(ct));
            var json = req.RequestUri!.AbsolutePath.EndsWith("/merge")
                ? "{}"
                : "{\"number\":1,\"title\":\"t\",\"state\":\"closed\",\"merged\":true}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
