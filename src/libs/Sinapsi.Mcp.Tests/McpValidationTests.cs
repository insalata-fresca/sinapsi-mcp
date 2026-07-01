// Input-validation matrix for the library's public-API inputs, exercised both
// directly against the internal McpValidation helper and end-to-end through the
// public CallToolAsync seam. Proves that a missing / malformed public input is
// rejected with a clear, named reason BEFORE any network I/O happens (no HTTP
// handler call is made). NUL is expressed with the C# escape \0 -- never a
// literal NUL byte -- so this file diffs as text. Banners are plain ASCII.
using Sinapsi.Mcp;
using Xunit;

namespace Sinapsi.Mcp.Tests;

public class McpValidationTests
{
    // A handler that fails the test if it is ever invoked: validation must reject
    // the call before any request is sent.
    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("network I/O attempted despite invalid input");
    }

    private static GatewayMcpClient NoNetworkClient() =>
        new(new HttpClient(new NeverCalledHandler()));

    private static readonly Uri Gateway = new("https://upstream.test/mcp");

    // ---- gateway Uri ----------------------------------------------------------

    [Fact]
    public void RequireGateway_rejects_null()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => McpValidation.RequireGateway(null));
        Assert.Equal("gateway", ex.ParamName);
    }

    [Fact]
    public void RequireGateway_rejects_relative_uri()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            McpValidation.RequireGateway(new Uri("/mcp", UriKind.Relative)));
        Assert.Equal("gateway", ex.ParamName);
    }

    [Theory]
    [InlineData("ftp://host/mcp")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ws://host/mcp")]
    public void RequireGateway_rejects_non_http_scheme(string uri)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            McpValidation.RequireGateway(new Uri(uri)));
        Assert.Equal("gateway", ex.ParamName);
        Assert.Contains("scheme", ex.Message);
    }

    [Theory]
    [InlineData("http://host/mcp")]
    [InlineData("https://host:9200/mcp")]
    public void RequireGateway_accepts_http_and_https(string uri)
    {
        var result = McpValidation.RequireGateway(new Uri(uri));
        Assert.Equal(uri, result.ToString());
    }

    // ---- bearer ---------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireBearer_rejects_missing(string? bearer)
    {
        var ex = Assert.Throws<ArgumentException>(() => McpValidation.RequireBearer(bearer));
        Assert.Equal("bearerJwt", ex.ParamName);
    }

    [Theory]
    [InlineData("tok\0en")] // embedded NUL
    [InlineData("tok\nen")] // embedded newline would corrupt the Authorization header
    [InlineData("tok\ren")]
    public void RequireBearer_rejects_control_chars(string bearer)
    {
        var ex = Assert.Throws<ArgumentException>(() => McpValidation.RequireBearer(bearer));
        Assert.Equal("bearerJwt", ex.ParamName);
        Assert.Contains("control", ex.Message);
    }

    [Fact]
    public void RequireBearer_rejects_overlong()
    {
        var huge = new string('a', McpValidation.MaxBearerLength + 1);
        var ex = Assert.Throws<ArgumentException>(() => McpValidation.RequireBearer(huge));
        Assert.Equal("bearerJwt", ex.ParamName);
        Assert.Contains("too long", ex.Message);
    }

    // ---- tool name ------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RequireToolName_rejects_missing(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(() => McpValidation.RequireToolName(name));
        Assert.Equal("toolName", ex.ParamName);
    }

    [Theory]
    [InlineData("echo\0")]
    [InlineData("ec\nho")]
    public void RequireToolName_rejects_control_chars(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => McpValidation.RequireToolName(name));
        Assert.Equal("toolName", ex.ParamName);
    }

    [Fact]
    public void RequireToolName_rejects_overlong()
    {
        var huge = new string('t', McpValidation.MaxToolNameLength + 1);
        var ex = Assert.Throws<ArgumentException>(() => McpValidation.RequireToolName(huge));
        Assert.Contains("too long", ex.Message);
    }

    // ---- end-to-end through the public seam -----------------------------------

    [Fact]
    public async Task CallToolAsync_rejects_relative_gateway_before_any_io()
    {
        var client = NoNetworkClient();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CallToolAsync(new Uri("/mcp", UriKind.Relative), "jwt", "echo", new { }, CancellationToken.None));
    }

    [Fact]
    public async Task CallToolAsync_rejects_blank_bearer_before_any_io()
    {
        var client = NoNetworkClient();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CallToolAsync(Gateway, "  ", "echo", new { }, CancellationToken.None));
    }

    [Fact]
    public async Task CallToolAsync_rejects_control_char_tool_name_before_any_io()
    {
        var client = NoNetworkClient();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CallToolAsync(Gateway, "jwt", "e\0cho", new { }, CancellationToken.None));
    }
}
