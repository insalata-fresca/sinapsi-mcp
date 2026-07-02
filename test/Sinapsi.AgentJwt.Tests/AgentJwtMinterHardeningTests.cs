using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Sinapsi.AgentJwt;
using Xunit;

namespace Sinapsi.AgentJwt.Tests;

// Plain-ASCII comment banner so this file diffs as TEXT.
//
// End-to-end hardening coverage driven through the AgentJwtMinter public
// surface: the agent-name guard fires BEFORE any filesystem access, the
// fail-closed options check fires at the mint seam, and a secret that a
// provider (or a malformed key) would surface is [redacted] in the thrown
// exception -- the signing key is never echoed.

public sealed class AgentJwtMinterHardeningTests : IDisposable
{
    private readonly string _dir;
    private readonly RSA _rsa;

    public AgentJwtMinterHardeningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "agentjwt-harden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _rsa = RSA.Create(2048);
        WriteJwk("agent1", _rsa.ExportPkcs8PrivateKeyPem());
    }

    private void WriteJwk(string agent, string keyPem)
    {
        var jwk = new { keyId = "kid-abc", userId = "user-123", key = keyPem, type = "serviceaccount" };
        File.WriteAllText(Path.Combine(_dir, $"{agent}.json"), JsonSerializer.Serialize(jwk));
    }

    private AgentJwtOptions Options(string issuer = "https://id.test", string audience = "proj-123") => new()
    {
        KeyDir = _dir,
        Issuer = issuer,
        AudienceProjectId = audience,
        TtlMinutes = 15,
    };

    // A handler that must never be reached (proves the guard short-circuits I/O).
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("network must not be reached");
        }
    }

    private sealed class RespondHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond());
    }

    // ---- agent-name guard fires before filesystem / network --------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../secrets")]
    [InlineData("sub/agent")]
    [InlineData("agent\0name")]     // NUL, C# escape
    [InlineData("..")]
    public async Task MintAsync_RejectsMalformedAgent_BeforeAnyIo(string agent)
    {
        var handler = new ExplodingHandler();
        var minter = new AgentJwtMinter(new HttpClient(handler), Options());

        await Assert.ThrowsAsync<ArgumentException>(() => minter.MintAsync(agent, CancellationToken.None));
        Assert.Equal(0, handler.Calls); // no network I/O attempted
    }

    // ---- fail-closed options fire at the mint seam -----------------------

    [Fact]
    public async Task MintAsync_MissingAudience_ThrowsNamingTheOption()
    {
        var minter = new AgentJwtMinter(new HttpClient(new ExplodingHandler()), Options(audience: ""));
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => minter.MintAsync("agent1", CancellationToken.None));
        Assert.Contains("OIDC_AUDIENCE_PROJECT_ID", ex.Message);
    }

    [Fact]
    public async Task MintAsync_NonHttpIssuer_ThrowsNamingTheOption()
    {
        var minter = new AgentJwtMinter(new HttpClient(new ExplodingHandler()), Options(issuer: "ftp://id.test"));
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => minter.MintAsync("agent1", CancellationToken.None));
        Assert.Contains("OIDC_ISSUER", ex.Message);
    }

    // ---- a provider secret in the error is redacted (never echoed) -------

    [Fact]
    public async Task MintAsync_ProviderErrorWithSecret_IsRedactedInMessage()
    {
        // The token endpoint replies non-2xx with a body that (pathologically)
        // echoes a bearer token + a private key. Neither may reach the caller.
        const string leaked =
            "error: assertion rejected. Authorization: Bearer eyJLEAKED.tok.sig ; " +
            "-----BEGIN PRIVATE KEY-----MIIleakedKeyBytes-----END PRIVATE KEY-----";
        var handler = new RespondHandler(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(leaked),
        });
        var minter = new AgentJwtMinter(new HttpClient(handler), Options());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => minter.MintAsync("agent1", CancellationToken.None));

        Assert.Contains("401", ex.Message);              // the diagnostic survives
        Assert.Contains("[redacted]", ex.Message);       // the secrets do not
        Assert.DoesNotContain("eyJLEAKED.tok.sig", ex.Message);
        Assert.DoesNotContain("MIIleakedKeyBytes", ex.Message);
        Assert.DoesNotContain("PRIVATE KEY", ex.Message);
    }

    // ---- a malformed signing key is never echoed -------------------------

    [Fact]
    public async Task MintAsync_MalformedSigningKey_NeverEchoesTheKey()
    {
        const string bogusKey =
            "-----BEGIN PRIVATE KEY-----\nNOTVALIDBASE64!!leaked-secret-bytes\n-----END PRIVATE KEY-----";
        WriteJwk("badkey", bogusKey);
        var minter = new AgentJwtMinter(new HttpClient(new ExplodingHandler()), Options());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => minter.MintAsync("badkey", CancellationToken.None));

        Assert.DoesNotContain("leaked-secret-bytes", ex.Message);
        Assert.DoesNotContain("PRIVATE KEY", ex.Message);
        Assert.Contains("badkey", ex.Message); // agent named for diagnosability
    }

    public void Dispose()
    {
        _rsa.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
