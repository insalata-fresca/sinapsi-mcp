using System.Text.Json;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// L1 unit tests for the CT-side file/local adapters (repo transcript/bundle/link-resolver stores,
/// access log, pin store, enrollment-source provider). These run fully OFFLINE against real temp
/// directories — no network, no DB, no personal audio. Each mirrors its in-memory/fake contract, so
/// the same pipeline behaviour holds live. L2 verifies these against the real CT146 working tree +
/// the live external-blob fetch (drive:///gmail://) behind the pin store.
/// </summary>
public sealed class CtSideAdapterTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "cervello-l1-" + Guid.NewGuid().ToString("N"));

    public CtSideAdapterTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ } }

    // ── RepoTranscriptStore ────────────────────────────────────────────────────
    [Fact]
    public async Task Transcript_store_writes_at_the_schemas_8_path_and_is_write_once()
    {
        var store = new RepoTranscriptStore(_tmp);
        Assert.Equal("recordings/transcripts/rec-1.md", store.TranscriptPath("rec-1"));
        Assert.False(await store.ExistsAsync("rec-1"));

        var rel = await store.WriteBaseAsync("rec-1", new BaseTranscript("bonjour", "fr"));
        Assert.Equal("recordings/transcripts/rec-1.md", rel);
        Assert.True(await store.ExistsAsync("rec-1"));
        Assert.Equal("bonjour", await File.ReadAllTextAsync(Path.Combine(_tmp, rel)));

        // Write-once: a second write does NOT overwrite (the correction stage never clobbers the base).
        await store.WriteBaseAsync("rec-1", new BaseTranscript("REWRITTEN", "fr"));
        Assert.Equal("bonjour", await File.ReadAllTextAsync(Path.Combine(_tmp, rel)));
    }

    // ── RepoBundleStore ────────────────────────────────────────────────────────
    [Fact]
    public async Task Bundle_store_writes_inbox_pair_and_refuses_overwrite()
    {
        var store = new RepoBundleStore(_tmp);
        Assert.Equal("inbox/bnd-1/data.json", store.BundlePath("bnd-1", "data.json"));

        await store.WriteAsync("bnd-1", "{\"k\":1}", "# bundle");
        Assert.True(await store.ExistsAsync("bnd-1"));
        Assert.Equal("{\"k\":1}", await File.ReadAllTextAsync(Path.Combine(_tmp, "inbox", "bnd-1", "data.json")));
        Assert.Equal("# bundle", await File.ReadAllTextAsync(Path.Combine(_tmp, "inbox", "bnd-1", "bundle.md")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync("bnd-1", "x", "y"));
    }

    // ── RepoLinkResolver ───────────────────────────────────────────────────────
    [Fact]
    public async Task Link_resolver_detects_an_existing_dossier_and_a_missing_one()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "map", "people"));
        await File.WriteAllTextAsync(Path.Combine(_tmp, "map", "people", "guilhem.md"), "---\ntype: person\n---\n");
        var resolver = new RepoLinkResolver(_tmp);

        Assert.True(await resolver.DossierExistsAsync("guilhem"));
        Assert.False(await resolver.DossierExistsAsync("nobody"));   // → R4 stub declaration upstream
    }

    // ── CtAccessLog ────────────────────────────────────────────────────────────
    [Fact]
    public async Task Access_log_appends_a_redacted_json_line_with_no_body_fields()
    {
        var path = Path.Combine(_tmp, "access.log");
        var log = new CtAccessLog(path);

        await log.AppendAsync(new AccessLogEntry("cervello_open_points_answer", "cervello", "applied", "op-1"));
        await log.AppendAsync(new AccessLogEntry("cervello_open_points_list", "cervello", "ok"));

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("cervello_open_points_answer", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal("cervello", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal("op-1", doc.RootElement.GetProperty("point_id").GetString());
        // R10: only redacted fields exist — no body/audio/vector keys.
        Assert.False(doc.RootElement.TryGetProperty("body", out _));
        Assert.False(doc.RootElement.TryGetProperty("vector", out _));
    }

    // ── CtPinStore ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Pin_store_hashes_the_fetched_bytes_and_content_addresses_the_blob()
    {
        var pinDir = Path.Combine(_tmp, "pins");
        var bytes = System.Text.Encoding.UTF8.GetBytes("external evidence body");
        var fetcher = new StubBlobFetcher(bytes);
        var store = new CtPinStore(fetcher, pinDir);

        var sha = await store.PinAsync("drive://abc123");

        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(expected, sha);
        Assert.True(File.Exists(Path.Combine(pinDir, sha)));      // content-addressed blob written on-CT
        Assert.Equal("drive://abc123", Assert.Single(fetcher.Fetched));
    }

    // ── DiarizedCentroidEnrollmentSourceProvider ───────────────────────────────
    [Fact]
    public async Task Enrollment_source_returns_a_registered_centroid_and_null_for_unknown()
    {
        var provider = new DiarizedCentroidEnrollmentSourceProvider();
        var src = new EnrollmentSource(TestVectors.Axis(0), ["rec://rec-1#s1"], 0.7);
        provider.Register("rec-1", "s1", src);

        Assert.Same(src, await provider.GetConfirmedSourceAsync("rec-1", "s1"));
        Assert.Null(await provider.GetConfirmedSourceAsync("rec-1", "s2"));   // unknown → enroll skipped

        provider.Evict("rec-1", "s1");
        Assert.Null(await provider.GetConfirmedSourceAsync("rec-1", "s1"));   // custody: transient, evicted
    }

    private sealed class StubBlobFetcher(byte[] bytes) : IExternalBlobFetcher
    {
        public List<string> Fetched { get; } = [];
        public Task<ReadOnlyMemory<byte>> FetchAsync(string externalRef, CancellationToken ct = default)
        {
            Fetched.Add(externalRef);
            return Task.FromResult<ReadOnlyMemory<byte>>(bytes);
        }
    }
}
