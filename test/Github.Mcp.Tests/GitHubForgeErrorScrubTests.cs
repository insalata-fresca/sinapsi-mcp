using System.Net;
using System.Text;
using Github.Mcp.Forge;
using Sinapsi.Forge;
using Sinapsi.Forge.Tools;
using Xunit;

namespace Github.Mcp.Tests;

/// <summary>
/// Load-bearing proof for the GitHub adapter: an error response body that leaks a credential
/// is scrubbed to <c>[redacted]</c> before it reaches the exception message a tool surfaces —
/// the RAW HTTP status is preserved as the verdict, the secret is not. Mirrors the Gitea-side
/// coverage so BOTH hosts are pinned.
/// </summary>
public sealed class GitHubForgeErrorScrubTests
{
    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static GitHubForgeClient Client(HttpStatusCode status, string body)
        => new(new HttpClient(new FixedResponseHandler(status, body)) { BaseAddress = new Uri("https://api.github.com/") });

    [Fact]
    public async Task Upstream_error_body_with_a_token_is_scrubbed_in_the_exception_message()
    {
        const string secret = "ghp_GHSECRETTOKEN98765";
        var client = Client(HttpStatusCode.InternalServerError, $"{{\"message\":\"boom token={secret}\"}}");

        var ex = await Assert.ThrowsAsync<ForgeApiException>(() => client.GetRepoAsync("o", "r"));

        Assert.Equal(500, ex.Status);                 // RAW status verdict preserved
        Assert.Contains("[redacted]", ex.Message);
        Assert.DoesNotContain(secret, ex.Message);    // the raw secret never survives
    }

    [Fact]
    public void Sanitize_helper_is_shared_and_redacts_bearer_headers()
    {
        var scrubbed = SinapsiForgeErrors.Sanitize("403: Authorization: Bearer eyJ.LEAKPAYLOAD.sig");
        Assert.Contains("[redacted]", scrubbed);
        Assert.DoesNotContain("LEAKPAYLOAD", scrubbed);
    }
}
