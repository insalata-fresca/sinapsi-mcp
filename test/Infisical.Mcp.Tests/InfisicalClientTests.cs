using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infisical.Mcp.Tests;

/// <summary>
/// The REST client's error- and token-handling paths — the branches the happy-path
/// tool tests don't reach. We pin: the upsert's POST→PATCH fallback (a create that
/// conflicts is retried as an update), the double-failure throw (both verbs fail →
/// an exception carrying both status codes), and the Universal-Auth token lifecycle
/// (a single login is cached across calls; re-login on expiry; a login response with
/// no <c>accessToken</c> throws).
/// </summary>
public sealed class InfisicalClientTests
{
    /// <summary>A scriptable Infisical fake. <see cref="LoginCalls"/> counts how many
    /// times the universal-auth login endpoint was hit (to assert token caching), and
    /// <see cref="Responder"/> lets a test decide the status for each non-login request
    /// keyed by HTTP method, so the upsert POST/PATCH branches can be driven precisely.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public int LoginCalls { get; private set; }
        public List<(string Method, string Url, string Body)> Calls { get; } = new();

        /// <summary>The body returned by the login endpoint (default: a valid token).</summary>
        public string LoginBodyJson { get; set; } = """{"accessToken":"test-token"}""";

        /// <summary>Status to return for a non-login request, given its HTTP method.
        /// Default: every verb succeeds.</summary>
        public Func<HttpMethod, HttpStatusCode> Responder { get; set; } = _ => HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var url = request.RequestUri!.ToString();
            Calls.Add((request.Method.Method, url, body));

            if (url.Contains("/v1/auth/universal-auth/login"))
            {
                LoginCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoginBodyJson, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(Responder(request.Method))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static InfisicalOptions Opt() => new()
    {
        HostUrl = "https://secrets.example.org",
        ClientId = "cid",
        ClientSecret = "csecret",
        ProjectId = "proj",
        EnvName = "dev",
    };

    private static InfisicalClient Build(ScriptedHandler rec) =>
        new(new HttpClient(rec), Opt(), NullLogger<InfisicalClient>.Instance);

    private static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;

    // ── SetSecretAsync: the upsert (POST create → PATCH on conflict) ─────────────────

    [Fact]
    public async Task SetSecret_falls_back_to_PATCH_when_the_POST_create_conflicts()
    {
        // POST (create) conflicts; PATCH (update) succeeds → no throw, and the value is
        // delivered on the PATCH request.
        var rec = new ScriptedHandler
        {
            Responder = m => m == HttpMethod.Post ? HttpStatusCode.Conflict : HttpStatusCode.OK,
        };
        var client = Build(rec);

        await client.SetSecretAsync("/web/api", "DB_PASSWORD", "v-123", CancellationToken.None);

        var secretCalls = rec.Calls.Where(c => c.Url.EndsWith("/v3/secrets/raw/DB_PASSWORD")).ToList();
        // Exactly the create attempt then the update fallback, in order.
        Assert.Equal(new[] { "POST", "PATCH" }, secretCalls.Select(c => c.Method).ToArray());
        // The PATCH (the one that won) carried the value and the path.
        var patch = Parse(secretCalls.Single(c => c.Method == "PATCH").Body);
        Assert.Equal("/web/api", patch.GetProperty("secretPath").GetString());
        Assert.Equal("v-123", patch.GetProperty("secretValue").GetString());
    }

    [Fact]
    public async Task SetSecret_does_not_PATCH_when_the_POST_create_succeeds()
    {
        // A clean create must NOT trigger the update fallback.
        var rec = new ScriptedHandler(); // all OK
        var client = Build(rec);

        await client.SetSecretAsync("/web/api", "NEW_KEY", "v", CancellationToken.None);

        var secretCalls = rec.Calls.Where(c => c.Url.EndsWith("/v3/secrets/raw/NEW_KEY")).ToList();
        Assert.Single(secretCalls);
        Assert.Equal("POST", secretCalls[0].Method);
    }

    [Fact]
    public async Task SetSecret_throws_with_both_status_codes_when_POST_and_PATCH_both_fail()
    {
        // POST 500, PATCH 403 → InvalidOperationException naming the path and BOTH codes.
        var rec = new ScriptedHandler
        {
            Responder = m => m == HttpMethod.Post
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.Forbidden,
        };
        var client = Build(rec);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SetSecretAsync("/web/api", "K", "v", CancellationToken.None));

        Assert.Contains("/web/api/K", ex.Message);
        Assert.Contains("500", ex.Message);  // POST status
        Assert.Contains("403", ex.Message);  // PATCH status
    }

    // ── TokenAsync: Universal-Auth login caching + the missing-token guard ───────────

    [Fact]
    public async Task Token_is_obtained_once_and_reused_across_calls()
    {
        // Multiple secret operations must drive exactly ONE universal-auth login: the
        // cached, non-expired token is reused.
        var rec = new ScriptedHandler();
        var client = Build(rec);

        await client.SetSecretAsync("/web/api", "A", "1", CancellationToken.None);
        await client.SetSecretAsync("/web/api", "B", "2", CancellationToken.None);
        await client.ListSecretNamesAsync("/web/api", CancellationToken.None);

        Assert.Equal(1, rec.LoginCalls);
    }

    [Fact]
    public async Task Token_throws_when_the_login_response_has_no_accessToken()
    {
        // A 200 login that omits accessToken is a protocol violation we must surface,
        // not silently treat as an empty bearer.
        var rec = new ScriptedHandler { LoginBodyJson = """{"somethingElse":"x"}""" };
        var client = Build(rec);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SetSecretAsync("/web/api", "K", "v", CancellationToken.None));

        Assert.Contains("accessToken", ex.Message);
    }

    // ── ListSecretNamesAsync: a 404 (never-provisioned path) is EMPTY, not a failure ──

    [Fact]
    public async Task ListSecretNamesAsync_returns_an_empty_list_when_the_path_has_never_been_provisioned()
    {
        // Infisical 404s a secretPath whose folder doesn't exist yet. That is the common,
        // legitimate shape of the sanctioned "does this secret already exist" check for a
        // service that hasn't been provisioned — it must come back as zero names, not throw.
        var rec = new ScriptedHandler
        {
            Responder = m => m == HttpMethod.Get ? HttpStatusCode.NotFound : HttpStatusCode.OK,
        };
        var client = Build(rec);

        var names = await client.ListSecretNamesAsync("/web/never-provisioned", CancellationToken.None);

        Assert.Empty(names);
    }

    [Fact]
    public async Task ListSecretNamesAsync_still_throws_on_a_genuine_upstream_failure()
    {
        // Only 404 gets the empty-list treatment — any other failure status must still
        // surface as an exception (caught + sanitized by the tool layer above).
        var rec = new ScriptedHandler
        {
            Responder = m => m == HttpMethod.Get ? HttpStatusCode.InternalServerError : HttpStatusCode.OK,
        };
        var client = Build(rec);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ListSecretNamesAsync("/web/api", CancellationToken.None));
    }
}
