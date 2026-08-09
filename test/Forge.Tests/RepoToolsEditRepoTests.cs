using System.Net;
using System.Text;
using System.Text.Json;
using Sinapsi.Forge;
using Sinapsi.Forge.Gitea;
using Sinapsi.Forge.Tools;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Drives the REAL <c>edit_repo</c> tool over the Gitea/Forgejo adapter with a fake transport,
/// proving the <c>default_delete_branch_after_merge</c> flag is threaded tool → request → PATCH
/// body. Enabling it is what stops merged branches accumulating without bound; the null case is
/// the load-bearing one, because a payload that always carried the field would silently reset the
/// repo's existing setting on every unrelated edit.
/// </summary>
public sealed class RepoToolsEditRepoTests
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

    private static (IForgeClient client, CapturingHandler handler) Make()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"full_name":"ste/sinapsi-mcp","private":false}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://forge.example/api/v1/") };
        return (new GiteaForgeClient(http), handler);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task edit_repo_tool_forwards_the_flag_when_set(bool value)
    {
        var (client, handler) = Make();

        await RepoTools.EditRepo(client, "ste", "sinapsi-mcp", default_delete_branch_after_merge: value);

        Assert.Equal(HttpMethod.Patch, handler.Request!.Method);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal(value, doc.RootElement.GetProperty("default_delete_branch_after_merge").GetBoolean());
    }

    [Fact]
    public async Task edit_repo_tool_omits_the_flag_when_not_passed()
    {
        var (client, handler) = Make();

        await RepoTools.EditRepo(client, "ste", "sinapsi-mcp", description: "unrelated edit");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("unrelated edit", doc.RootElement.GetProperty("description").GetString());
        Assert.False(doc.RootElement.TryGetProperty("default_delete_branch_after_merge", out _));
    }

    // ── homepage ─────────────────────────────────────────────────────────────
    // Forgejo/Gitea calls the homepage `website`; GitHub calls it `homepage`. The tool
    // parameter is `homepage` on both, and each adapter maps to its own wire name — the
    // same split already handled for default_delete_branch_after_merge above.

    [Fact]
    public async Task edit_repo_tool_maps_homepage_to_the_gitea_website_field()
    {
        var (client, handler) = Make();

        await RepoTools.EditRepo(client, "ste", "sinapsi-mcp", homepage: "https://example.test/x");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("https://example.test/x", doc.RootElement.GetProperty("website").GetString());
        Assert.False(doc.RootElement.TryGetProperty("homepage", out _));
    }

    [Fact]
    public async Task edit_repo_tool_omits_website_when_homepage_is_not_passed()
    {
        var (client, handler) = Make();

        await RepoTools.EditRepo(client, "ste", "sinapsi-mcp", description: "unrelated edit");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.False(doc.RootElement.TryGetProperty("website", out _));
    }

    [Fact]
    public async Task get_repo_surfaces_the_website_as_homepage()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"full_name":"ste/sinapsi-mcp","website":"https://example.test/x"}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://forge.example/api/v1/") };

        var repo = await new GiteaForgeClient(http).GetRepoAsync("ste", "sinapsi-mcp");

        Assert.Equal("https://example.test/x", repo.Homepage);
    }
}
