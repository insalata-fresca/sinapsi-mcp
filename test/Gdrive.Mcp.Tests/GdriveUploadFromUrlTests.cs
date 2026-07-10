using Xunit;

namespace Gdrive.Mcp.Tests;

/// <summary>
/// Coverage for <c>upload_from_url</c> — the server-side-streaming upload path added so a large
/// binary can go INTO Drive without any base64 through the model context (the inbound mirror of
/// <c>download_to_url</c>). Three legs, all provable without a live Google account or network:
///   1. <see cref="GdriveValidation.ValidateFetchUrl"/> accept/reject matrix, including the SSRF
///      guard (link-local / metadata refused; private + loopback allowed).
///   2. Short-circuit: a bad parameter returns a structured {ok:false,error} envelope BEFORE any
///      HTTP fetch (proven with an <see cref="IHttpClientFactory"/> that throws if ever used).
///   3. The fail-closed upload config knobs (timeout ceiling + size cap).
/// </summary>
public sealed class GdriveUploadFromUrlValidationTests
{
    [Theory]
    [InlineData("http://10.42.0.102/artifact.pdf")]   // homelab LAN host — allowed
    [InlineData("https://example.com/book.pdf")]        // external https — allowed
    [InlineData("http://127.0.0.1:8080/local.bin")]    // loopback — allowed (a service on the MCP host)
    [InlineData("http://[::1]:9000/x")]                  // IPv6 loopback — allowed
    public void ValidateFetchUrl_AcceptsReachableHttpUrls(string url) =>
        Assert.Null(GdriveValidation.ValidateFetchUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateFetchUrl_RejectsEmpty(string? url) =>
        Assert.Equal("url is required", GdriveValidation.ValidateFetchUrl(url));

    [Theory]
    [InlineData("ftp://host/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://host/x")]
    public void ValidateFetchUrl_RejectsNonHttpScheme(string url) =>
        Assert.Equal("url scheme must be http or https", GdriveValidation.ValidateFetchUrl(url));

    [Fact]
    public void ValidateFetchUrl_RejectsNonAbsolute() =>
        Assert.Equal("url is not a valid absolute URL", GdriveValidation.ValidateFetchUrl("not-a-url"));

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud metadata (link-local)
    [InlineData("http://169.254.0.1/x")]                       // link-local range
    public void ValidateFetchUrl_RejectsLinkLocalMetadata(string url) =>
        Assert.Equal("url host must not be a link-local / metadata address", GdriveValidation.ValidateFetchUrl(url));

    [Fact]
    public void ValidateFetchUrl_RejectsUnspecifiedAddress() =>
        Assert.Equal("url host must not be the unspecified address", GdriveValidation.ValidateFetchUrl("http://0.0.0.0/x"));

    [Fact]
    public void ValidateFetchUrl_RejectsControlChars() =>
        Assert.Contains("control characters", GdriveValidation.ValidateFetchUrl("http://host/\n")!);

    [Fact]
    public void ValidateFetchUrl_RejectsTooLong()
    {
        var url = "http://host/" + new string('a', GdriveValidation.MaxUrlLength);
        Assert.Contains("too long", GdriveValidation.ValidateFetchUrl(url)!);
    }
}

/// <summary>Short-circuit proof: invalid input returns the error envelope before any fetch runs.</summary>
public sealed class GdriveUploadFromUrlGuardTests
{
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("upload_from_url must not fetch when validation fails");
    }

    private static (bool ok, string? error) Envelope(object result)
    {
        var t = result.GetType();
        var ok = t.GetProperty("ok") is { } okp && (bool)okp.GetValue(result)!;
        var err = t.GetProperty("error")?.GetValue(result) as string;
        return (ok, err);
    }

    // A GdriveConfig with the fail-closed defaults, no env needed.
    private static GdriveConfig Cfg() => GdriveConfig.FromEnvironment();

    [Fact]
    public async Task BadName_ShortCircuits_NoFetch()
    {
        var r = await DriveTools.UploadFromUrl(
            FakeDrive.Throwing(), new ThrowingHttpClientFactory(), Cfg(),
            name: "   ", url: "http://10.42.0.102/x.pdf", mimeType: "application/pdf");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("name is required", err);
    }

    [Fact]
    public async Task BadUrlScheme_ShortCircuits_NoFetch()
    {
        var r = await DriveTools.UploadFromUrl(
            FakeDrive.Throwing(), new ThrowingHttpClientFactory(), Cfg(),
            name: "x.pdf", url: "file:///etc/passwd", mimeType: "application/pdf");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("url scheme must be http or https", err);
    }

    [Fact]
    public async Task LinkLocalUrl_ShortCircuits_NoFetch()
    {
        var r = await DriveTools.UploadFromUrl(
            FakeDrive.Throwing(), new ThrowingHttpClientFactory(), Cfg(),
            name: "x", url: "http://169.254.169.254/latest/meta-data/", mimeType: "application/octet-stream");
        var (ok, err) = Envelope(r);
        Assert.False(ok);
        Assert.Equal("url host must not be a link-local / metadata address", err);
    }
}

/// <summary>Fail-closed config for the upload knobs. Mutates env → the shared non-parallel collection.</summary>
[Collection("env")]
public sealed class GdriveUploadConfigTests : IDisposable
{
    private const string TimeoutVar = "GDRIVE_MCP_UPLOAD_TIMEOUT_SECONDS";
    private const string MaxBytesVar = "GDRIVE_MCP_UPLOAD_MAX_BYTES";

    public GdriveUploadConfigTests() => Clear();
    public void Dispose() => Clear();
    private static void Clear()
    {
        Environment.SetEnvironmentVariable(TimeoutVar, null);
        Environment.SetEnvironmentVariable(MaxBytesVar, null);
    }

    [Fact]
    public void Defaults_WhenUnset_AreTheDocumentedDefaults()
    {
        Clear();
        var cfg = GdriveConfig.FromEnvironment();
        Assert.Equal(GdriveConfig.DefaultUploadTimeoutSeconds, cfg.UploadTimeoutSeconds);
        Assert.Equal(GdriveConfig.DefaultUploadMaxBytes, cfg.UploadMaxBytes);
    }

    [Fact]
    public void ValidValues_AreBound()
    {
        Environment.SetEnvironmentVariable(TimeoutVar, "1200");
        Environment.SetEnvironmentVariable(MaxBytesVar, "5368709120"); // 5 GiB
        var cfg = GdriveConfig.FromEnvironment();
        Assert.Equal(1200, cfg.UploadTimeoutSeconds);
        Assert.Equal(5_368_709_120L, cfg.UploadMaxBytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("nope")]
    [InlineData("999999")] // above the 7200 s ceiling
    public void BadTimeout_Throws_NamingTheVar(string bad)
    {
        Environment.SetEnvironmentVariable(TimeoutVar, bad);
        var ex = Assert.Throws<InvalidOperationException>(() => GdriveConfig.FromEnvironment());
        Assert.Contains(TimeoutVar, ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("huge")]
    public void BadMaxBytes_Throws_NamingTheVar(string bad)
    {
        Environment.SetEnvironmentVariable(MaxBytesVar, bad);
        var ex = Assert.Throws<InvalidOperationException>(() => GdriveConfig.FromEnvironment());
        Assert.Contains(MaxBytesVar, ex.Message);
    }
}
