using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Sinapsi.Indexer;
using Sinapsi.Nats;
using Xunit;

namespace Sinapsi.Indexer.Tests;

/// <summary>
/// Regression cover for the live crashloop (Npgsql 22021 "invalid byte sequence for
/// encoding UTF8: 0x00", thrown from PostgresIndexStore.UpsertAsync during a full
/// reindex, then StopHost-killing the whole service). Two independent defences:
///   (1) the scanner SKIPS any file whose content carries a NUL (0x00) byte, so the
///       poison never reaches the store — the OTHER docs still index; and
///   (2) even if a poison doc slips through, the per-doc upsert is isolated so a
///       PostgresException on ONE doc is logged + skipped and the rest of the repo's
///       docs still index (no throw out of the background loop → no crashloop), while
///       a genuinely fatal store error (NpgsqlException) still propagates.
/// </summary>
public sealed class NulByteResilienceTests
{
    // ── Defence 1: scanner NUL detection ─────────────────────────────────────────

    [Fact]
    public void HasNulByte_detects_a_nul_and_passes_clean_text()
    {
        Assert.True(GitSourceScanner.HasNulByte("before\0after"));
        Assert.True(GitSourceScanner.HasNulByte("\0"));
        Assert.False(GitSourceScanner.HasNulByte("perfectly normal markdown"));
        Assert.False(GitSourceScanner.HasNulByte(""));
    }

    [Fact]
    public void Scan_skips_a_nul_byte_doc_but_still_indexes_the_others()
    {
        var dir = Path.Combine(Path.GetTempPath(), "indexer-nul-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Two clean markdown files + one that carries a NUL byte in its body
            // (a mislabelled binary / corrupt checkout committed as *.md).
            File.WriteAllText(Path.Combine(dir, "good-a.md"), "# Alpha\nclean body A");
            File.WriteAllText(Path.Combine(dir, "good-b.md"), "# Beta\nclean body B");
            File.WriteAllText(Path.Combine(dir, "poison.md"), "# Poison\nbad\0byte here");

            var scanner = new GitSourceScanner(Array.Empty<RepoSpec>(), token: null, NullLogger.Instance);
            var repo = new RepoSpec("docs", "https://example/docs.git", "main", dir);

            var docs = scanner.Scan(repo);

            var paths = docs.Select(d => d.Path).OrderBy(p => p).ToArray();
            Assert.Equal(new[] { "good-a.md", "good-b.md" }, paths);
            Assert.DoesNotContain(docs, d => d.Path == "poison.md");
            // And nothing that DID index carries a NUL (the store would reject it).
            Assert.All(docs, d => Assert.DoesNotContain('\0', d.Body));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Defence 2: per-doc upsert isolation in the worker ────────────────────────

    private static IndexerWorker NewWorker(IIndexStore store)
    {
        // Constructed WITHOUT touching NATS (ExecuteAsync is never called). A default
        // NatsConnectionOptions is enough to satisfy the base constructor.
        var scanner = new GitSourceScanner(Array.Empty<RepoSpec>(), token: null, NullLogger.Instance);
        return new IndexerWorker(store, scanner, new FakeEmbedder(),
            new NatsConnectionOptions(), NullLogger<IndexerWorker>.Instance);
    }

    private static Document Doc(string source, string path, string body) => new()
    {
        DocId = Document.MakeDocId(source, path),
        Source = source,
        Path = path,
        Kind = "doc",
        Title = path,
        Body = body,
        ContentSha = GitSourceScanner.Sha256(body),
    };

    private static PostgresException Nul22021() =>
        // The exact class Postgres raises for a NUL in a text parameter.
        new("invalid byte sequence for encoding \"UTF8\": 0x00",
            severity: "ERROR", invariantSeverity: "ERROR", sqlState: "22021");

    [Fact]
    public async Task IndexDocs_skips_a_doc_whose_upsert_throws_PostgresException_and_indexes_the_rest()
    {
        var poison = Document.MakeDocId("docs", "poison.md");
        var store = new RecordingIndexStore(poison, Nul22021);
        var worker = NewWorker(store);

        var docs = new[]
        {
            Doc("docs", "a.md", "clean A"),
            Doc("docs", "poison.md", "bad\0byte"),
            Doc("docs", "b.md", "clean B"),
            Doc("docs", "c.md", "clean C"),
        };

        // MUST NOT throw — one poison doc cannot abort the scan (the live crashloop).
        var ex = await Record.ExceptionAsync(() => worker.IndexDocsAsync("docs", docs, CancellationToken.None));
        Assert.Null(ex);

        // The other three docs were upserted; the poison one was not.
        Assert.Equal(
            new[] { "docs:a.md", "docs:b.md", "docs:c.md" },
            store.Upserted.OrderBy(x => x).ToArray());
        Assert.DoesNotContain(poison, store.Upserted);

        // The tombstone pass still ran, and the poison DocId is in the present set
        // (a transient failure must NOT cause it to be wrongly tombstoned).
        Assert.Equal("docs", store.TombstoneSource);
        Assert.Contains(poison, store.TombstonePresent!);
    }

    [Fact]
    public async Task IndexDocs_lets_a_fatal_store_error_propagate()
    {
        // A NON-Postgres store failure (connection down / auth) is genuinely fatal —
        // it must NOT be swallowed, so the outer retry/backoff can handle it.
        var poison = Document.MakeDocId("docs", "b.md");
        var store = new RecordingIndexStore(poison,
            () => new NpgsqlException("connection refused: the data tier is down"));
        var worker = NewWorker(store);

        var docs = new[]
        {
            Doc("docs", "a.md", "clean A"),
            Doc("docs", "b.md", "clean B"),
        };

        await Assert.ThrowsAsync<NpgsqlException>(
            () => worker.IndexDocsAsync("docs", docs, CancellationToken.None));
    }

    // ── P2a bar: the tools sanitize the very error this bug throws ────────────────

    [Fact]
    public void P2a_the_22021_upsert_error_is_sanitized_and_carries_no_credential()
    {
        // If a Postgres error string ever reaches a caller it must be scrubbed +
        // length-capped and leak no password/token (P2a). Prove the sanitizer is
        // clean on the exact exception this bug raises AND on a leaky variant.
        var clean = IndexerErrors.FromException(Nul22021());
        Assert.Contains("22021", clean);           // still diagnosable
        Assert.DoesNotContain("password", clean, StringComparison.OrdinalIgnoreCase);

        var leaky = IndexerErrors.FromException(
            new NpgsqlException("upsert failed: password=hunter2-supersecret host=db"));
        Assert.DoesNotContain("hunter2-supersecret", leaky);
        Assert.Contains("[redacted]", leaky);
        Assert.True(leaky.Length <= IndexerErrors.MaxErrorLength);
    }
}
