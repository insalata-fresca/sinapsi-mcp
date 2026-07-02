using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sinapsi.AgentJwt;
using Xunit;

namespace Sinapsi.AgentJwt.Tests;

/// <summary>
/// Exercises the <see cref="AgentJwtMinter"/> public surface end-to-end with a
/// stub token endpoint and a real RSA-signed JWK on disk: assertion shape, form
/// fields, token extraction, the per-agent cache, and HTTP-error handling.
/// </summary>
public sealed class AgentJwtMinterTests : IDisposable
{
    private readonly string _dir;
    private readonly RSA _rsa;
    private const string UserId = "user-123";
    private const string KeyId = "kid-abc";

    public AgentJwtMinterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "agentjwt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _rsa = RSA.Create(2048);
        WriteJwk("agent1");
    }

    private void WriteJwk(string agent)
    {
        var jwk = new
        {
            keyId = KeyId,
            userId = UserId,
            key = _rsa.ExportPkcs8PrivateKeyPem(),
            type = "serviceaccount",
        };
        File.WriteAllText(Path.Combine(_dir, $"{agent}.json"), JsonSerializer.Serialize(jwk));
    }

    private AgentJwtOptions Options(string projectId = "555", int ttl = 15) => new()
    {
        KeyDir = _dir,
        Issuer = "https://id.test",
        AudienceProjectId = projectId,
        TtlMinutes = ttl,
    };

    // ---- a capturing stub handler -----------------------------------------

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;
        public Uri? LastUri;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request, LastBody ?? "");
        }
    }

    private static HttpResponseMessage Ok(string accessToken) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { access_token = accessToken })),
    };

    [Fact]
    public async Task MintAsync_ReturnsAccessTokenFromProvider()
    {
        var stub = new StubHandler((_, _) => Ok("ACCESS-TOKEN-XYZ"));
        var minter = new AgentJwtMinter(new HttpClient(stub), Options());

        var token = await minter.MintAsync("agent1", CancellationToken.None);

        Assert.Equal("ACCESS-TOKEN-XYZ", token);
        Assert.Equal(1, stub.Calls);
        Assert.Equal(new Uri("https://id.test/oauth/v2/token"), stub.LastUri);
    }

    [Fact]
    public async Task MintAsync_PostsJwtBearerGrantAndProjectScope()
    {
        var stub = new StubHandler((_, _) => Ok("t"));
        var minter = new AgentJwtMinter(new HttpClient(stub), Options(projectId: "777"));

        await minter.MintAsync("agent1", CancellationToken.None);

        var body = Uri.UnescapeDataString(stub.LastBody!);
        Assert.Contains("grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer", body);
        Assert.Contains("urn:zitadel:iam:org:project:id:777:aud", body);
        Assert.Contains("assertion=", body);
    }

    [Fact]
    public async Task MintAsync_BuildsValidRs256AssertionWithExpectedClaims()
    {
        string capturedAssertion = "";
        var stub = new StubHandler((_, body) =>
        {
            // assertion=<jwt>&grant_type=... — pull the assertion value out.
            foreach (var kv in body.Split('&'))
            {
                var i = kv.IndexOf('=');
                if (i > 0 && kv[..i] == "assertion")
                    capturedAssertion = Uri.UnescapeDataString(kv[(i + 1)..]);
            }
            return Ok("t");
        });
        var minter = new AgentJwtMinter(new HttpClient(stub), Options());

        await minter.MintAsync("agent1", CancellationToken.None);

        var parts = capturedAssertion.Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonSerializer.Deserialize<JsonElement>(B64UrlDecode(parts[0]));
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal(KeyId, header.GetProperty("kid").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());

        var payload = JsonSerializer.Deserialize<JsonElement>(B64UrlDecode(parts[1]));
        Assert.Equal(UserId, payload.GetProperty("iss").GetString());
        Assert.Equal(UserId, payload.GetProperty("sub").GetString());
        Assert.Equal("https://id.test", payload.GetProperty("aud").GetString());
        var iat = payload.GetProperty("iat").GetInt64();
        var exp = payload.GetProperty("exp").GetInt64();
        Assert.Equal(15 * 60, exp - iat); // TtlMinutes honoured

        // Signature verifies against the agent's public key.
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var sig = B64UrlDecode(parts[2]);
        Assert.True(_rsa.VerifyData(signingInput, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task MintAsync_CachesTokenAcrossCalls()
    {
        var stub = new StubHandler((_, _) => Ok("cached"));
        var minter = new AgentJwtMinter(new HttpClient(stub), Options());

        var a = await minter.MintAsync("agent1", CancellationToken.None);
        var b = await minter.MintAsync("agent1", CancellationToken.None);

        Assert.Equal("cached", a);
        Assert.Equal("cached", b);
        Assert.Equal(1, stub.Calls); // second call served from cache
    }

    [Fact]
    public async Task MintAsync_DifferentAgentsMintSeparately()
    {
        WriteJwk("agent2");
        var n = 0;
        var stub = new StubHandler((_, _) => Ok("tok-" + n++));
        var minter = new AgentJwtMinter(new HttpClient(stub), Options());

        var a1 = await minter.MintAsync("agent1", CancellationToken.None);
        var a2 = await minter.MintAsync("agent2", CancellationToken.None);

        Assert.NotEqual(a1, a2);
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task MintAsync_ThrowsOnProviderHttpError()
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\"}"),
        });
        var minter = new AgentJwtMinter(new HttpClient(stub), Options());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => minter.MintAsync("agent1", CancellationToken.None));
        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid_grant", ex.Message);
    }

    [Fact]
    public async Task MintAsync_ThrowsWhenJwkMissing()
    {
        var stub = new StubHandler((_, _) => Ok("t"));
        var minter = new AgentJwtMinter(new HttpClient(stub), Options());

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => minter.MintAsync("nope", CancellationToken.None));
    }

    private static byte[] B64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
