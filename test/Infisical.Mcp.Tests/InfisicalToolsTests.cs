using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infisical.Mcp.Tests;

/// <summary>
/// The tool surface, exercised against a recording fake of the Infisical REST API. The
/// invariant under test is the transcript-safety design: for generated material the secret
/// VALUE is produced server-side, stored, and NEVER returned to the caller — only the path,
/// the bytes count, or a public key comes back. We also pin the path shape and that the
/// upserted value actually reaches the store.
/// </summary>
public sealed class InfisicalToolsTests
{
    /// <summary>Captures every outbound request body and answers the Infisical endpoints
    /// the client touches (login -> token; folder create; secret upsert; secret list).</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body)> Calls { get; } = new();
        public string ListResponseJson { get; set; } = """{"secrets":[]}""";

        /// <summary>Status returned for the GET /v3/secrets/raw (list) call only. Everything
        /// else (login, folder create, secret upsert) always answers 200 — the 404-vs-empty
        /// behavior under test belongs to the list call alone.</summary>
        public HttpStatusCode ListStatusCode { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var url = request.RequestUri!.ToString();
            Calls.Add((request.Method.Method, url, body));

            if (url.Contains("/v3/secrets/raw") && request.Method == HttpMethod.Get && ListStatusCode != HttpStatusCode.OK)
                return new HttpResponseMessage(ListStatusCode)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };

            string json;
            if (url.Contains("/v1/auth/universal-auth/login"))
                json = """{"accessToken":"test-token"}""";
            else if (url.Contains("/v3/secrets/raw") && request.Method == HttpMethod.Get)
                json = ListResponseJson;
            else
                json = "{}"; // folder create + secret upsert: success is enough

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (InfisicalTools tools, RecordingHandler rec) Build(string? listJson = null)
    {
        var rec = new RecordingHandler();
        if (listJson is not null) rec.ListResponseJson = listJson;
        var http = new HttpClient(rec);
        var opt = new InfisicalOptions
        {
            HostUrl = "https://secrets.example.org",
            ClientId = "cid",
            ClientSecret = "csecret",
            ProjectId = "proj",
            EnvName = "dev",
        };
        var client = new InfisicalClient(http, opt, NullLogger<InfisicalClient>.Instance);
        return (new InfisicalTools(client, opt), rec);
    }

    private static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task Issue_nats_nkey_returns_only_the_public_key_and_stores_the_seed_server_side()
    {
        var (tools, rec) = Build();

        var resultJson = await tools.issue_nats_nkey("web", "api", CancellationToken.None);
        var result = Parse(resultJson);

        // Returned material is non-secret only: a U-prefixed public key + the path.
        var pub = result.GetProperty("public_key").GetString()!;
        Assert.StartsWith("U", pub);
        Assert.Equal("/web/api/NATS_NKEY_SEED", result.GetProperty("path").GetString());
        Assert.Equal("dev", result.GetProperty("env").GetString());

        // The seed is the property that must NOT appear in the caller-facing result.
        Assert.False(result.TryGetProperty("seed", out _));

        // ... but it MUST have been written to the store. Find the secret-upsert call and
        // confirm a non-empty NATS seed (S-prefixed) value was sent — and that this seed
        // value is NOT the public key we returned.
        var upsert = rec.Calls.Single(c => c.Url.EndsWith("/v3/secrets/raw/NATS_NKEY_SEED"));
        var sent = Parse(upsert.Body);
        Assert.Equal("/web/api", sent.GetProperty("secretPath").GetString());
        var seed = sent.GetProperty("secretValue").GetString()!;
        Assert.StartsWith("S", seed);            // NATS seeds are Base32 S...-prefixed
        Assert.NotEqual(pub, seed);
        Assert.DoesNotContain(seed, resultJson); // the seed never transits the result
    }

    [Fact]
    public async Task Issue_random_secret_stores_hex_of_requested_length_and_hides_the_value()
    {
        var (tools, rec) = Build();

        var resultJson = await tools.issue_random_secret("web", "api", "DB_PASSWORD", bytes: 16, CancellationToken.None);
        var result = Parse(resultJson);

        Assert.Equal("/web/api/DB_PASSWORD", result.GetProperty("stored").GetString());
        Assert.Equal(16, result.GetProperty("bytes").GetInt32());
        Assert.False(result.TryGetProperty("value", out _));

        var upsert = rec.Calls.Single(c => c.Url.EndsWith("/v3/secrets/raw/DB_PASSWORD"));
        var value = Parse(upsert.Body).GetProperty("secretValue").GetString()!;
        Assert.Equal(32, value.Length);                       // 16 bytes -> 32 hex chars
        Assert.Matches("^[0-9a-f]+$", value);                 // lowercase hex
        Assert.DoesNotContain(value, resultJson);             // value not leaked back
    }

    [Fact]
    public async Task Issue_random_secret_defaults_to_32_bytes_when_non_positive()
    {
        var (tools, rec) = Build();

        var result = Parse(await tools.issue_random_secret("g", "s", "K", bytes: 0, CancellationToken.None));
        Assert.Equal(32, result.GetProperty("bytes").GetInt32());

        var upsert = rec.Calls.Single(c => c.Url.EndsWith("/v3/secrets/raw/K"));
        var value = Parse(upsert.Body).GetProperty("secretValue").GetString()!;
        Assert.Equal(64, value.Length);                       // 32 bytes -> 64 hex chars
    }

    [Fact]
    public async Task Set_secret_stores_the_caller_supplied_value_at_the_path()
    {
        var (tools, rec) = Build();

        var result = Parse(await tools.set_secret("web", "api", "VENDOR_TOKEN", "tok-123", CancellationToken.None));
        Assert.Equal("/web/api/VENDOR_TOKEN", result.GetProperty("stored").GetString());

        var upsert = rec.Calls.Single(c => c.Url.EndsWith("/v3/secrets/raw/VENDOR_TOKEN"));
        var sent = Parse(upsert.Body);
        Assert.Equal("/web/api", sent.GetProperty("secretPath").GetString());
        Assert.Equal("tok-123", sent.GetProperty("secretValue").GetString());
    }

    [Fact]
    public async Task List_secrets_returns_names_only_parsed_from_the_secrets_array()
    {
        var (tools, _) = Build(
            listJson: """{"secrets":[{"secretKey":"DB_PASSWORD"},{"secretKey":"NATS_NKEY_SEED"}]}""");

        var result = Parse(await tools.list_secrets("web", "api", CancellationToken.None));

        Assert.Equal("/web/api", result.GetProperty("path").GetString());
        var names = result.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "DB_PASSWORD", "NATS_NKEY_SEED" }, names);
    }

    [Fact]
    public async Task List_secrets_returns_an_empty_success_envelope_when_the_path_has_never_been_provisioned()
    {
        // Root-cause regression test (CanonFix-tools #53): a 404 from Infisical for a
        // secretPath that has no folder yet must come back as a normal, successful
        // {path, names:[]} envelope — NOT an "ok:false" error and NOT an unhandled MCP
        // exception. This is exactly the sanctioned "does this secret already exist" check.
        var (tools, rec) = Build();
        rec.ListStatusCode = HttpStatusCode.NotFound;

        var resultJson = await tools.list_secrets("web", "never-provisioned", CancellationToken.None);
        var result = Parse(resultJson);

        Assert.Equal("/web/never-provisioned", result.GetProperty("path").GetString());
        Assert.Empty(result.GetProperty("names").EnumerateArray());
        Assert.False(result.TryGetProperty("ok", out _)); // success shape, not the {ok:false,error} envelope
    }

    [Fact]
    public async Task Ensure_folder_creates_the_group_then_the_service_under_it()
    {
        var (tools, rec) = Build();

        await tools.set_secret("web", "api", "K", "v", CancellationToken.None);

        var folderCreates = rec.Calls.Where(c => c.Url.EndsWith("/v1/folders")).Select(c => Parse(c.Body)).ToList();
        Assert.Equal(2, folderCreates.Count);
        // group folder at root, then service folder under the group.
        Assert.Equal("web", folderCreates[0].GetProperty("name").GetString());
        Assert.Equal("/", folderCreates[0].GetProperty("path").GetString());
        Assert.Equal("api", folderCreates[1].GetProperty("name").GetString());
        Assert.Equal("/web", folderCreates[1].GetProperty("path").GetString());
    }
}
