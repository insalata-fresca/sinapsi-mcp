using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cervello.Watcher;
using Cervello.Watcher.Drive;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// M6-refine: McpGdriveClient (IDriveClient over the gdrive MCP via the
/// agentgateway, replacing the Google-SA GoogleDriveClient). Scripts the MCP
/// JSON-RPC round-trip (initialize/notifications/tools-call, mirroring
/// GatewayMcpClientTests) behind a real AgentJwtMinter backed by a stub OIDC
/// token endpoint + a real on-disk JWK — end-to-end through the exact seam
/// production code uses, no shortcuts.
/// </summary>
public sealed class McpGdriveClientTests : IDisposable
{
    private static readonly Uri Gateway = new("https://gw.test/mcp");
    private readonly string _keyDir;
    private readonly RSA _rsa = RSA.Create(2048);

    public McpGdriveClientTests()
    {
        _keyDir = Path.Combine(Path.GetTempPath(), "mcpgdrive-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_keyDir);
        var jwk = new { keyId = "kid-1", userId = "user-1", key = _rsa.ExportPkcs8PrivateKeyPem(), type = "serviceaccount" };
        File.WriteAllText(Path.Combine(_keyDir, "agent-cervello-watcher.json"), JsonSerializer.Serialize(jwk));
    }

    // ---- OIDC token stub (backs AgentJwtMinter) ----
    private sealed class OidcStub : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { access_token = "jwt-" + Calls })),
            };
            return Task.FromResult(res);
        }
    }

    // ---- MCP gateway stub: scripts one response per tools/call, records every "arguments" sent ----
    private sealed class GatewayStub(Queue<object> toolResults) : HttpMessageHandler
    {
        public List<JsonElement> ToolCalls { get; } = new(); // one per tools/call, {name, arguments}
        // When set, the NEXT tools/call returns this HTTP status (transport-layer failure)
        // instead of a scripted tool result — simulates a 5xx from the gateway itself.
        public HttpStatusCode? FailNextCallWithStatus;
        // When > 0, the next N tools/call attempts return this status before a scripted
        // result is served — simulates a transient gateway failure that recovers (M6-finish
        // bounded-retry test). Each consumed failure decrements the counter.
        public int FailFirstNCallsWithStatusCount;
        public HttpStatusCode FailFirstNCallsWithStatus = HttpStatusCode.ServiceUnavailable;
        public int ToolCallAttempts; // every tools/call attempt, including failed ones
        private int _seq;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var method = doc.RootElement.GetProperty("method").GetString();

            if (method == "initialize")
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                res.Headers.TryAddWithoutValidation("Mcp-Session-Id", "sess-" + (++_seq));
                return res;
            }
            if (method == "notifications/initialized")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

            // tools/call
            ToolCallAttempts++;
            if (FailNextCallWithStatus is { } status)
            {
                FailNextCallWithStatus = null;
                return new HttpResponseMessage(status) { Content = new StringContent("upstream unavailable") };
            }
            if (FailFirstNCallsWithStatusCount > 0)
            {
                FailFirstNCallsWithStatusCount--;
                return new HttpResponseMessage(FailFirstNCallsWithStatus) { Content = new StringContent("upstream transiently unavailable") };
            }
            ToolCalls.Add(doc.RootElement.GetProperty("params").Clone());
            var payload = toolResults.Count > 0 ? toolResults.Dequeue() : new { ok = false, error = "no scripted result" };
            var wrapped = new
            {
                jsonrpc = "2.0",
                id = 2,
                result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload) } } },
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(wrapped), Encoding.UTF8, "application/json"),
            };
        }
    }

    private (McpGdriveClient client, GatewayStub gw, OidcStub oidc) Build(WatcherConfig? cfg, params object[] scriptedResults)
    {
        var gw = new GatewayStub(new Queue<object>(scriptedResults));
        var oidc = new OidcStub();
        var gateway = new GatewayMcpClient(new HttpClient(gw));
        var minter = new AgentJwtMinter(new HttpClient(oidc),
            new AgentJwtOptions { KeyDir = _keyDir, Issuer = "https://id.test", AudienceProjectId = "1" });
        var config = cfg ?? WatcherConfig.From(new Dictionary<string, string?>())
            with { GatewayUrl = Gateway.ToString(), FolderId = "folder-recordings" };
        return (new McpGdriveClient(gateway, minter, config), gw, oidc);
    }

    private static string ToolName(JsonElement callParams) => callParams.GetProperty("name").GetString()!;
    private static JsonElement Args(JsonElement callParams) => callParams.GetProperty("arguments");

    // ---- GetStartPageTokenAsync ----

    [Fact]
    public async Task GetStartPageToken_returns_an_empty_snapshot_with_no_gateway_call()
    {
        var (client, gw, _) = Build(null);
        var token = await client.GetStartPageTokenAsync(default);
        Assert.NotNull(token);
        Assert.Empty(gw.ToolCalls); // bootstrap does not call the gateway at all
    }

    // ---- ListChangesAsync: new-file detection ----

    [Fact]
    public async Task ListChanges_detects_new_files_as_non_removed_changes()
    {
        var (client, gw, _) = Build(null,
            new { count = 1, files = new[] { new { id = "A", name = "Foo.m4a", mimeType = "audio/mp4", md5Checksum = "abc", size = 3, parents = new[] { "folder-recordings" }, trashed = false } } });

        var start = await client.GetStartPageTokenAsync(default);
        var page = await client.ListChangesAsync(start, default);

        Assert.Single(page.Changes);
        Assert.Equal("A", page.Changes[0].FileId);
        Assert.False(page.Changes[0].Removed);
        Assert.Equal("abc", page.Changes[0].Md5);
        Assert.Contains("folder-recordings", page.Changes[0].Parents);

        Assert.Single(gw.ToolCalls);
        // WIRE name is gdrive_-prefixed: the agentgateway federates the gdrive backend and
        // splits the tool name on the FIRST underscore into target="gdrive" + bare tool
        // "list_files". The PEP CEL rule matches the BARE name (mcp.tool.name in [...]); the
        // CLIENT must send the prefixed federated name. Asserting the bare name here (the M6
        // pre-refine assumption) is what made this red — the production prefix is correct.
        Assert.Equal("gdrive_list_files", ToolName(gw.ToolCalls[0]));
        Assert.Equal("folder-recordings", Args(gw.ToolCalls[0]).GetProperty("folderId").GetString());
    }

    [Fact]
    public async Task ListChanges_second_cycle_with_unchanged_md5_yields_no_changes()
    {
        var (client, gw, _) = Build(null,
            new { count = 1, files = new[] { new { id = "A", name = "Foo.m4a", mimeType = "audio/mp4", md5Checksum = "abc", size = 3, parents = new[] { "folder-recordings" }, trashed = false } } },
            new { count = 1, files = new[] { new { id = "A", name = "Foo.m4a", mimeType = "audio/mp4", md5Checksum = "abc", size = 3, parents = new[] { "folder-recordings" }, trashed = false } } });

        var start = await client.GetStartPageTokenAsync(default);
        var page1 = await client.ListChangesAsync(start, default);
        Assert.Single(page1.Changes);

        var page2 = await client.ListChangesAsync(page1.NewStartPageToken!, default);
        Assert.Empty(page2.Changes); // same fileId + same md5 -> no-op, matches Drive Changes semantics
    }

    [Fact]
    public async Task ListChanges_md5_change_on_known_file_is_surfaced_as_a_change()
    {
        var (client, gw, _) = Build(null,
            new { count = 1, files = new[] { new { id = "A", name = "Foo.m4a", mimeType = "audio/mp4", md5Checksum = "abc", size = 3, parents = new[] { "folder-recordings" }, trashed = false } } },
            new { count = 1, files = new[] { new { id = "A", name = "Foo.m4a", mimeType = "audio/mp4", md5Checksum = "XYZ-changed", size = 9, parents = new[] { "folder-recordings" }, trashed = false } } });

        var start = await client.GetStartPageTokenAsync(default);
        var page1 = await client.ListChangesAsync(start, default);
        var page2 = await client.ListChangesAsync(page1.NewStartPageToken!, default);

        Assert.Single(page2.Changes);
        Assert.Equal("XYZ-changed", page2.Changes[0].Md5);
        Assert.False(page2.Changes[0].Removed);
    }

    [Fact]
    public async Task ListChanges_file_disappearing_from_listing_is_surfaced_as_removed()
    {
        var (client, gw, _) = Build(null,
            new { count = 1, files = new[] { new { id = "A", name = "Foo.m4a", mimeType = "audio/mp4", md5Checksum = "abc", size = 3, parents = new[] { "folder-recordings" }, trashed = false } } },
            new { count = 0, files = Array.Empty<object>() });

        var start = await client.GetStartPageTokenAsync(default);
        var page1 = await client.ListChangesAsync(start, default);
        var page2 = await client.ListChangesAsync(page1.NewStartPageToken!, default);

        Assert.Single(page2.Changes);
        Assert.Equal("A", page2.Changes[0].FileId);
        Assert.True(page2.Changes[0].Removed);
    }

    [Fact]
    public async Task ListChanges_throws_when_folder_id_is_not_resolved()
    {
        var cfg = WatcherConfig.From(new Dictionary<string, string?>()) with { GatewayUrl = Gateway.ToString(), FolderId = null };
        var (client, _, _) = Build(cfg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListChangesAsync("token", default));
    }

    [Fact]
    public async Task ListChanges_mints_a_jwt_per_call_via_the_configured_agent()
    {
        var (client, _, oidc) = Build(null,
            new { count = 0, files = Array.Empty<object>() });
        var start = await client.GetStartPageTokenAsync(default);
        await client.ListChangesAsync(start, default);
        Assert.Equal(1, oidc.Calls); // one mint (cached thereafter) backing the list_files call
    }

    // ---- GetMetadataAsync ----

    [Fact]
    public async Task GetMetadata_returns_a_DriveChange_for_a_known_file()
    {
        var (client, gw, _) = Build(null,
            new { id = "A", name = "Foo.m4a", mimeType = "audio/mp4", md5Checksum = "abc", size = 3L, trashed = false });

        var change = await client.GetMetadataAsync("A", default);

        Assert.NotNull(change);
        Assert.Equal("A", change!.FileId);
        Assert.Equal("Foo.m4a", change.Name);
        Assert.Equal("gdrive_get_file_metadata", ToolName(gw.ToolCalls[0])); // federated wire name (PEP splits on first _)
        Assert.Equal("A", Args(gw.ToolCalls[0]).GetProperty("fileId").GetString());
    }

    [Fact]
    public async Task GetMetadata_returns_null_on_gdrive_error_envelope()
    {
        var (client, _, _) = Build(null, new { ok = false, error = "not found" });
        var change = await client.GetMetadataAsync("missing", default);
        Assert.Null(change);
    }

    // ---- DownloadMediaAsync ----

    [Fact]
    public async Task DownloadMedia_writes_a_single_chunk_when_eof_immediately()
    {
        var bytes = Encoding.UTF8.GetBytes("the audio bytes of Foo");
        var (client, gw, _) = Build(null,
            new { fileId = "A", offset = 0, returnedBytes = bytes.Length, totalSize = bytes.Length, encoding = "base64", content = Convert.ToBase64String(bytes), eof = true });

        using var ms = new MemoryStream();
        await client.DownloadMediaAsync("A", ms, default);

        Assert.Equal(bytes, ms.ToArray());
        Assert.Single(gw.ToolCalls);
        Assert.Equal("gdrive_download_file_base64", ToolName(gw.ToolCalls[0])); // federated wire name (PEP splits on first _)
        Assert.Equal(0, Args(gw.ToolCalls[0]).GetProperty("offset").GetInt64());
    }

    [Fact]
    public async Task DownloadMedia_loops_chunks_until_eof_and_reassembles_bytes_exactly()
    {
        var chunk1 = Encoding.UTF8.GetBytes("hello ");
        var chunk2 = Encoding.UTF8.GetBytes("world");
        var (client, gw, _) = Build(null,
            new { fileId = "A", offset = 0, returnedBytes = chunk1.Length, totalSize = chunk1.Length + chunk2.Length, encoding = "base64", content = Convert.ToBase64String(chunk1), eof = false },
            new { fileId = "A", offset = chunk1.Length, returnedBytes = chunk2.Length, totalSize = chunk1.Length + chunk2.Length, encoding = "base64", content = Convert.ToBase64String(chunk2), eof = true });

        using var ms = new MemoryStream();
        await client.DownloadMediaAsync("A", ms, default);

        Assert.Equal(Encoding.UTF8.GetBytes("hello world"), ms.ToArray());
        Assert.Equal(2, gw.ToolCalls.Count);
        Assert.Equal(0, Args(gw.ToolCalls[0]).GetProperty("offset").GetInt64());
        Assert.Equal(chunk1.Length, Args(gw.ToolCalls[1]).GetProperty("offset").GetInt64());
    }

    [Fact]
    public async Task DownloadMedia_throws_a_terminal_DriveMediaException_on_gdrive_error_envelope()
    {
        var (client, _, _) = Build(null, new { ok = false, error = "404 not found" });
        using var ms = new MemoryStream();
        var ex = await Assert.ThrowsAsync<DriveMediaException>(() => client.DownloadMediaAsync("gone", ms, default));
        Assert.Contains("not found", ex.Message);
        Assert.False(ex.Transient); // a resolved-but-bad gdrive request is terminal, not retryable
    }

    [Fact]
    public async Task DownloadMedia_throws_a_transient_DriveMediaException_on_gateway_5xx()
    {
        // No in-call retry (GdriveMaxRetries=0) — the single 5xx surfaces immediately as transient.
        var cfg = NoRetryCfg();
        var (client, gw, _) = Build(cfg);
        gw.FailNextCallWithStatus = HttpStatusCode.ServiceUnavailable;
        using var ms = new MemoryStream();
        var ex = await Assert.ThrowsAsync<DriveMediaException>(() => client.DownloadMediaAsync("A", ms, default));
        Assert.True(ex.Transient); // a gateway-level 5xx is worth retrying under the same key
    }

    // ---- M6-finish: bounded retry + timeout on TRANSIENT gdrive failures ----

    /// <summary>Config with retries but zero backoff — fast deterministic retry tests.</summary>
    private static WatcherConfig FastRetryCfg(int maxRetries) =>
        WatcherConfig.From(new Dictionary<string, string?>())
            with { GatewayUrl = Gateway.ToString(), FolderId = "folder-recordings", GdriveMaxRetries = maxRetries, GdriveRetryBackoffMs = 0 };

    /// <summary>Config with retries disabled — a transient failure surfaces on the first try.</summary>
    private static WatcherConfig NoRetryCfg() =>
        WatcherConfig.From(new Dictionary<string, string?>())
            with { GatewayUrl = Gateway.ToString(), FolderId = "folder-recordings", GdriveMaxRetries = 0, GdriveRetryBackoffMs = 0 };

    [Fact]
    public async Task ListChanges_retries_a_transient_5xx_then_succeeds()
    {
        var (client, gw, _) = Build(FastRetryCfg(3),
            new { count = 0, files = Array.Empty<object>() });
        gw.FailFirstNCallsWithStatusCount = 2; // two 5xx, then the scripted success

        var start = await client.GetStartPageTokenAsync(default);
        var page = await client.ListChangesAsync(start, default);

        Assert.Empty(page.Changes);
        Assert.Equal(3, gw.ToolCallAttempts); // 2 failed + 1 success — retried under the bound
        Assert.Single(gw.ToolCalls);          // only the successful attempt was recorded/parsed
    }

    [Fact]
    public async Task ListChanges_transient_5xx_exhausting_retries_propagates()
    {
        var (client, gw, _) = Build(FastRetryCfg(2)); // 2 retries => 3 attempts, all fail
        gw.FailFirstNCallsWithStatusCount = 99;

        var start = await client.GetStartPageTokenAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListChangesAsync(start, default));
        Assert.Equal(3, gw.ToolCallAttempts); // bounded at maxRetries+1 — does NOT spin forever
    }

    [Fact]
    public async Task DownloadMedia_retries_a_transient_5xx_then_reassembles_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes("recovered audio");
        var (client, gw, _) = Build(FastRetryCfg(3),
            new { fileId = "A", offset = 0, returnedBytes = bytes.Length, totalSize = bytes.Length, encoding = "base64", content = Convert.ToBase64String(bytes), eof = true });
        gw.FailFirstNCallsWithStatusCount = 1; // one 5xx, then success

        using var ms = new MemoryStream();
        await client.DownloadMediaAsync("A", ms, default);

        Assert.Equal(bytes, ms.ToArray());
        Assert.Equal(2, gw.ToolCallAttempts); // 1 failed + 1 success
    }

    [Fact]
    public async Task DownloadMedia_terminal_gdrive_envelope_is_not_retried()
    {
        // {ok:false} is a 200 with an error envelope — terminal, must NOT consume retries.
        var (client, gw, _) = Build(FastRetryCfg(5), new { ok = false, error = "404 not found" });
        using var ms = new MemoryStream();
        var ex = await Assert.ThrowsAsync<DriveMediaException>(() => client.DownloadMediaAsync("gone", ms, default));
        Assert.False(ex.Transient);
        Assert.Equal(1, gw.ToolCallAttempts); // exactly one attempt — no retry on a terminal envelope
    }

    // ---- ResolveFolderIdAsync ----

    [Fact]
    public async Task ResolveFolderId_returns_configured_FolderId_without_a_gateway_call()
    {
        var cfg = WatcherConfig.From(new Dictionary<string, string?>()) with { GatewayUrl = Gateway.ToString(), FolderId = "already-known" };
        var (client, gw, _) = Build(cfg);
        var id = await client.ResolveFolderIdAsync(default);
        Assert.Equal("already-known", id);
        Assert.Empty(gw.ToolCalls);
    }

    [Fact]
    public async Task ResolveFolderId_searches_by_leaf_name_when_FolderId_unset()
    {
        var cfg = WatcherConfig.From(new Dictionary<string, string?>()) with { GatewayUrl = Gateway.ToString(), FolderId = null, FolderPath = "cervello/recordings" };
        var (client, gw, _) = Build(cfg, new { count = 1, files = new[] { new { id = "resolved-folder-id" } } });

        var id = await client.ResolveFolderIdAsync(default);

        Assert.Equal("resolved-folder-id", id);
        Assert.Equal("gdrive_search_files", ToolName(gw.ToolCalls[0])); // federated wire name (PEP splits on first _)
        Assert.Contains("recordings", Args(gw.ToolCalls[0]).GetProperty("query").GetString());
    }

    [Fact]
    public async Task ResolveFolderId_throws_when_no_folder_matches()
    {
        var cfg = WatcherConfig.From(new Dictionary<string, string?>()) with { GatewayUrl = Gateway.ToString(), FolderId = null };
        var (client, _, _) = Build(cfg, new { count = 0, files = Array.Empty<object>() });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ResolveFolderIdAsync(default));
    }

    [Fact]
    public async Task ResolveFolderId_throws_when_ambiguous()
    {
        var cfg = WatcherConfig.From(new Dictionary<string, string?>()) with { GatewayUrl = Gateway.ToString(), FolderId = null };
        var (client, _, _) = Build(cfg, new { count = 2, files = new[] { new { id = "one" }, new { id = "two" } } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ResolveFolderIdAsync(default));
    }

    public void Dispose()
    {
        _rsa.Dispose();
        try { Directory.Delete(_keyDir, recursive: true); } catch { /* best effort */ }
    }
}
