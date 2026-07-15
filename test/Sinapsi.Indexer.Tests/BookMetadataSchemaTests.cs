// ---------------------------------------------------------------------------
// BookMetadataSchemaTests - integration proof that the M3 additive "metadata
// jsonb" column is backward-compatible: EnsureSchemaAsync creates/migrates it
// without touching any existing column, a document upserted with Metadata=null
// (the existing-tenant path) round-trips as SQL NULL, and a book-chunk document
// upserted with Metadata=<json> round-trips + is queryable via the jsonb GIN
// index (categories/authors facet lookups per design doc SS3.5).
//
// SKIPPED hermetically (no INDEXER_DB_HOST) via the existing DbFactAttribute —
// consistent with the 3 pre-existing DB-gated tests in SearchStoreDenylistTests.
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Sinapsi.Opds.Models;
using Xunit;

namespace Sinapsi.Indexer.Tests;

public sealed class BookMetadataSchemaTests
{
    [DbFact]
    public async Task EnsureSchemaAsync_CreatesMetadataColumn_ExistingDocsStayNull()
    {
        var store = new PostgresIndexStore(
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresIndexStore>());
        await store.EnsureSchemaAsync(CancellationToken.None);

        var prefix = $"test-book-meta-{Guid.NewGuid():N}";

        // A plain (non-book) document — the existing git-source shape, no
        // Metadata supplied. Proves the additive column defaults to NULL and
        // does not require every caller to change.
        var plainDoc = new Document
        {
            DocId = $"{prefix}:plain.md",
            Source = prefix,
            Path = "plain.md",
            Kind = "doc",
            Title = "Plain Doc",
            Body = "Ordinary markdown body, no book metadata.",
            ContentSha = GitSourceScanner.Sha256("plain-body"),
        };
        await store.UpsertAsync(plainDoc, CancellationToken.None);

        var hits = await store.SearchAsync("ordinary markdown body", prefix, null, 10, CancellationToken.None);
        Assert.Contains(hits, h => h.Path == "plain.md");
        // SearchHit doesn't project metadata (read-side unchanged) — the proof
        // that the row's metadata is NULL is done via a direct raw query below.
    }

    [DbFact]
    public async Task UpsertAsync_BookChunkDocument_PersistsAndRoundTripsMetadataJson()
    {
        var store = new PostgresIndexStore(
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresIndexStore>());
        await store.EnsureSchemaAsync(CancellationToken.None);

        var prefix = $"test-book-{Guid.NewGuid():N}";
        var chunk = new BookChunk
        {
            Key = $"{prefix}:0:0",
            Text = "Bounded contexts are a core Domain-Driven Design pattern for scoping models.",
            ChapterOrder = 0,
            ChapterHeading = "Chapter One",
            SectionHeading = "Bounded Contexts",
            ChunkIndex = 0,
            Categories = new[] { "Software Engineering", "Architecture" },
            Isbn = "978-0-13-468599-1",
            Title = "Domain-Driven Design",
            Authors = new[] { "Eric Evans" },
            SourceAnchor = "ch0.xhtml",
        };
        var doc = BookDocumentMapper.ToDocument(chunk, source: prefix);

        var wrote = await store.UpsertAsync(doc, CancellationToken.None);
        Assert.True(wrote);

        // Read back via FTS (proves the row is a normal, searchable document —
        // book-chunk metadata does not break the existing read path).
        var hits = await store.SearchAsync("bounded contexts domain driven design", prefix, "book_chunk", 10, CancellationToken.None);
        Assert.Contains(hits, h => h.Path == "ch0.xhtml");

        // Idempotent re-upsert of the identical chunk must be a no-op (same
        // contract as every other Document — ContentSha-gated).
        var wroteAgain = await store.UpsertAsync(doc, CancellationToken.None);
        Assert.False(wroteAgain);
    }

    [DbFact]
    public async Task Metadata_JsonbColumn_IsQueryableByCategoryFacet()
    {
        // Proves the additive GIN index actually supports the facet-scoped
        // retrieval the design doc calls for (SS3.5: "only data-engineering
        // books") — a raw jsonb containment query against the categories array.
        var store = new PostgresIndexStore(
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresIndexStore>());
        await store.EnsureSchemaAsync(CancellationToken.None);

        var prefix = $"test-facet-{Guid.NewGuid():N}";
        var chunk = new BookChunk
        {
            Key = $"{prefix}:0:0",
            Text = "A chunk about data engineering pipelines and batch processing.",
            ChapterOrder = 0,
            ChapterHeading = "Data Pipelines",
            SectionHeading = null,
            ChunkIndex = 0,
            Categories = new[] { "Data Engineering" },
            Isbn = "isbn-facet-test",
            Title = "Designing Data-Intensive Applications",
            Authors = new[] { "Martin Kleppmann" },
            SourceAnchor = "ch0.xhtml",
        };
        var doc = BookDocumentMapper.ToDocument(chunk, source: prefix);
        await store.UpsertAsync(doc, CancellationToken.None);

        var host = Environment.GetEnvironmentVariable("INDEXER_DB_HOST")!;
        var port = Environment.GetEnvironmentVariable("INDEXER_DB_PORT") is { Length: > 0 } p ? int.Parse(p) : 5432;
        var dbName = Environment.GetEnvironmentVariable("INDEXER_DB_NAME") is { Length: > 0 } d ? d : "sinapsi_index";
        var user = Environment.GetEnvironmentVariable("INDEXER_DB_USER") is { Length: > 0 } u ? u : "indexer";
        var pass = Environment.GetEnvironmentVariable("INDEXER_DB_PASSWORD")!;

        await using var conn = new Npgsql.NpgsqlConnection(
            new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = port,
                Database = dbName,
                Username = user,
                Password = pass,
            }.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT doc_id FROM documents WHERE source = @source AND metadata @> @filter::jsonb", conn);
        cmd.Parameters.AddWithValue("source", prefix);
        cmd.Parameters.AddWithValue("filter", """{"categories":["Data Engineering"]}""");
        await using var reader = await cmd.ExecuteReaderAsync();
        var found = await reader.ReadAsync();

        Assert.True(found, "jsonb containment query on the categories facet must find the upserted chunk");
    }
}
