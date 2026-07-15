using Microsoft.Extensions.Logging;
using Npgsql;

namespace Sinapsi.Indexer;

/// <summary>
/// Postgres tsvector implementation of <see cref="IIndexStore"/>. The full-text
/// vector is a STORED generated column (immutable to_tsvector('english'::regconfig, …))
/// with a GIN index, so writers never manage it and the read side just SELECTs against it.
/// Connection settings are entirely env-driven (INDEXER_DB_*), with neutral
/// local-host defaults.
/// </summary>
public sealed class PostgresIndexStore : IIndexStore
{
    private readonly string _connString;
    private readonly string _learningsSource;
    private readonly ILogger _log;

    public PostgresIndexStore(ILogger<PostgresIndexStore> log)
    {
        _log = log;
        var host = Env("INDEXER_DB_HOST", "127.0.0.1");
        var port = Env("INDEXER_DB_PORT", "5432");
        var db = Env("INDEXER_DB_NAME", "sinapsi_index");
        var user = Env("INDEXER_DB_USER", "indexer");
        var pass = Environment.GetEnvironmentVariable("INDEXER_DB_PASSWORD") ?? "";
        // Which logical source name holds the learnings corpus (drives get_learning).
        _learningsSource = Env("INDEXER_LEARNINGS_SOURCE", "learnings");
        _connString = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = int.Parse(port),
            Database = db,
            Username = user,
            Password = pass,
            SslMode = SslMode.Prefer,
            Pooling = true,
            MaxPoolSize = 10,
            Timeout = 15,
        }.ConnectionString;
    }

    private static string Env(string k, string dflt) =>
        Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : dflt;

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_connString);
        await c.OpenAsync(ct);
        return c;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS documents (
                doc_id      TEXT PRIMARY KEY,
                source      TEXT NOT NULL,
                path        TEXT NOT NULL,
                kind        TEXT NOT NULL,
                title       TEXT NOT NULL DEFAULT '',
                body        TEXT NOT NULL DEFAULT '',
                scope       TEXT NOT NULL DEFAULT '',
                content_sha TEXT NOT NULL,
                is_deleted  BOOLEAN NOT NULL DEFAULT FALSE,
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                tsv tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('english'::regconfig, coalesce(title, '')), 'A') ||
                    setweight(to_tsvector('english'::regconfig, coalesce(body, '')),  'B')
                ) STORED
            );
            CREATE INDEX IF NOT EXISTS idx_documents_tsv     ON documents USING GIN (tsv);
            CREATE INDEX IF NOT EXISTS idx_documents_source  ON documents (source) WHERE NOT is_deleted;
            CREATE INDEX IF NOT EXISTS idx_documents_kind    ON documents (kind)   WHERE NOT is_deleted;
            CREATE INDEX IF NOT EXISTS idx_documents_scope   ON documents (scope)  WHERE NOT is_deleted;
            -- Hybrid: pgvector embedding (all-MiniLM-L6-v2, 384-dim) + HNSW cosine index.
            ALTER TABLE documents ADD COLUMN IF NOT EXISTS embedding vector(384);
            CREATE INDEX IF NOT EXISTS idx_documents_embedding
                ON documents USING hnsw (embedding vector_cosine_ops);
            -- Additive (M3): per-document facet metadata (book chunks: isbn/authors/
            -- categories/chapter/heading/anchor; NULL for every existing git-source
            -- doc — zero behavior change for shared/career/cervello/learnings).
            -- Mirrors the "embedding" column precedent: ADD COLUMN IF NOT EXISTS,
            -- no rename/retype of any existing column.
            ALTER TABLE documents ADD COLUMN IF NOT EXISTS metadata jsonb;
            CREATE INDEX IF NOT EXISTS idx_documents_metadata
                ON documents USING GIN (metadata);
            """;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ddl, c);
        await cmd.ExecuteNonQueryAsync(ct);
        _log.LogInformation("schema ensured (documents + tsv GIN)");
    }

    public async Task<bool> UpsertAsync(Document doc, CancellationToken ct)
    {
        // Idempotent: only writes when the content hash differs (or the row was
        // tombstoned and is reappearing). No replay-doubling — re-processing the
        // same source is a no-op. Un-tombstones on a real change.
        const string sql = """
            INSERT INTO documents (doc_id, source, path, kind, title, body, scope, content_sha, metadata, is_deleted, updated_at)
            VALUES (@id, @source, @path, @kind, @title, @body, @scope, @sha, @metadata::jsonb, FALSE, now())
            ON CONFLICT (doc_id) DO UPDATE SET
                source = EXCLUDED.source, path = EXCLUDED.path, kind = EXCLUDED.kind,
                title = EXCLUDED.title, body = EXCLUDED.body, scope = EXCLUDED.scope,
                content_sha = EXCLUDED.content_sha, metadata = EXCLUDED.metadata,
                is_deleted = FALSE, updated_at = now(),
                embedding = NULL  -- content changed → re-embed (the backfill picks it up)
            WHERE documents.content_sha IS DISTINCT FROM EXCLUDED.content_sha
               OR documents.is_deleted;
            """;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", doc.DocId);
        cmd.Parameters.AddWithValue("source", doc.Source);
        cmd.Parameters.AddWithValue("path", doc.Path);
        cmd.Parameters.AddWithValue("kind", doc.Kind);
        cmd.Parameters.AddWithValue("title", doc.Title);
        cmd.Parameters.AddWithValue("body", doc.Body);
        cmd.Parameters.AddWithValue("scope", doc.Scope);
        cmd.Parameters.AddWithValue("sha", doc.ContentSha);
        // NULL for every existing caller (Document.Metadata defaults to null) —
        // additive, backward-compatible: non-book documents get a NULL jsonb cell.
        cmd.Parameters.AddWithValue("metadata", (object?)doc.Metadata ?? DBNull.Value);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<int> TombstoneMissingAsync(string source, IReadOnlyCollection<string> presentDocIds, CancellationToken ct)
    {
        const string sql = """
            UPDATE documents SET is_deleted = TRUE, updated_at = now()
            WHERE source = @source AND NOT is_deleted AND NOT (doc_id = ANY(@present));
            """;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("present", presentDocIds.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> TombstoneSourcesNotInAsync(IReadOnlyCollection<string> keepSources, CancellationToken ct)
    {
        // Fail-safe belongs to the CALLER (IndexerCore) per the IIndexStore
        // contract — but a defence-in-depth guard here costs nothing and
        // protects any other caller from wiping the whole store via an
        // empty keepSources.
        if (keepSources.Count == 0) return 0;
        const string sql = """
            UPDATE documents SET is_deleted = TRUE, updated_at = now()
            WHERE NOT is_deleted AND NOT (source = ANY(@keep));
            """;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("keep", keepSources.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT 1", c);
        await cmd.ExecuteScalarAsync(ct);
    }

    // Secret-denylist path fragments — mirrored from GitSourceScanner.DenyFragments so that
    // even if a secret-shaped row somehow reached the store (e.g. a manual INSERT or a
    // future scanner regression), it can never surface via the search read path.
    // Defence-in-depth: the scanner guards at ingest; the SQL guard protects the read path.
    //
    // NOTE: Document.Path is a REPO-RELATIVE path with NO leading slash (e.g.
    // "secrets/prod.yml", not "/secrets/prod.yml"). The leading-slash directory
    // fragments used by GitSourceScanner.DenyFragments (which prepends "/" before
    // matching) cannot be reused here verbatim — a LIKE pattern of '%/secrets/%'
    // misses top-level paths like "secrets/prod.yml" because there is no leading
    // slash in the stored value. Drop the leading slash from directory fragments so
    // '%secrets/%' matches both the top-level case ("secrets/prod.yml") and any
    // nested case ("config/secrets/db.md").
    private static readonly string[] SecretPathFragments =
        { "secrets/", "secret/", "vault.yml", "vault.yaml", ".git/", "private/" };

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, string? source, string? kind, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 30);
        var filters = "";
        if (!string.IsNullOrWhiteSpace(source)) filters += " AND source = @source";
        if (!string.IsNullOrWhiteSpace(kind)) filters += " AND kind = @kind";
        // Defence-in-depth: exclude secret-shaped paths IN THE SQL itself so they can
        // never appear in results even if somehow present in the store (e.g. manual insert
        // or future scanner regression). Paths are column values, never user-supplied, so
        // literal LIKE patterns are safe here — they are not parameterised user input.
        var denyConditions = string.Join("",
            SecretPathFragments.Select(f => $" AND path NOT LIKE '%{f.Replace("'", "''")}%'"));
        var sql = $"""
            SELECT source, path, kind, title, scope,
                   ts_headline('english'::regconfig, body, websearch_to_tsquery('english'::regconfig, @q),
                               'MaxFragments=2,MinWords=5,MaxWords=18') AS snippet,
                   ts_rank_cd(tsv, websearch_to_tsquery('english'::regconfig, @q)) AS score
            FROM documents
            WHERE NOT is_deleted
              AND tsv @@ websearch_to_tsquery('english'::regconfig, @q){filters}{denyConditions}
            ORDER BY score DESC LIMIT @lim
            """;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("q", query);
        cmd.Parameters.AddWithValue("lim", limit);
        if (!string.IsNullOrWhiteSpace(source)) cmd.Parameters.AddWithValue("source", source);
        if (!string.IsNullOrWhiteSpace(kind)) cmd.Parameters.AddWithValue("kind", kind);
        var hits = new List<SearchHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            hits.Add(new SearchHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetDouble(6)));
        return hits;
    }

    public async Task<IReadOnlyList<LearningHit>> GetLearningsAsync(string? scope, string? query, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 30);
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var filters = "";
        if (!string.IsNullOrWhiteSpace(scope)) filters += " AND scope = @scope";
        if (hasQuery) filters += " AND tsv @@ websearch_to_tsquery('english'::regconfig, @q)";
        var order = hasQuery
            ? "ts_rank_cd(tsv, websearch_to_tsquery('english'::regconfig, @q)) DESC"
            : "updated_at DESC";
        var sql = $"""
            SELECT path, title, scope, left(body, 600) AS excerpt, content_sha, updated_at
            FROM documents
            WHERE source = @learnings AND NOT is_deleted{filters}
            ORDER BY {order} LIMIT @lim
            """;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("learnings", _learningsSource);
        cmd.Parameters.AddWithValue("lim", limit);
        if (!string.IsNullOrWhiteSpace(scope)) cmd.Parameters.AddWithValue("scope", scope);
        if (hasQuery) cmd.Parameters.AddWithValue("q", query!);
        var hits = new List<LearningHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            hits.Add(new LearningHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)));
        return hits;
    }

    // pgvector text input: '[f1,f2,...]' (invariant culture, cast ::vector in SQL).
    private static string VecLiteral(float[] v) =>
        "[" + string.Join(",", v.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";

    public async Task SetEmbeddingAsync(string docId, float[] vector, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE documents SET embedding = @v::vector WHERE doc_id = @id", c);
        cmd.Parameters.AddWithValue("v", VecLiteral(vector));
        cmd.Parameters.AddWithValue("id", docId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<(string DocId, string Title, string Body)>> GetMissingEmbeddingsAsync(int limit, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT doc_id, title, body FROM documents WHERE embedding IS NULL AND NOT is_deleted LIMIT @n", c);
        cmd.Parameters.AddWithValue("n", limit);
        var outl = new List<(string, string, string)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            outl.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return outl;
    }

    public async Task<IReadOnlyList<SearchHit>> SemanticSearchAsync(float[] queryVector, string queryText, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 30);
        // Reciprocal Rank Fusion (k=60) of a vector ranking (cosine, <=>) and an
        // FTS ranking (ts_rank_cd). Either side may be empty; a doc present in
        // both ranks high. Snippet via ts_headline when the FTS matched.
        const string sql = """
            WITH v AS (
                SELECT doc_id, row_number() OVER (ORDER BY embedding <=> @qv::vector) AS rk
                FROM documents WHERE NOT is_deleted AND embedding IS NOT NULL
                ORDER BY embedding <=> @qv::vector LIMIT 50
            ),
            f AS (
                SELECT doc_id, row_number() OVER (ORDER BY ts_rank_cd(tsv, websearch_to_tsquery('english'::regconfig, @qt)) DESC) AS rk
                FROM documents
                WHERE NOT is_deleted AND tsv @@ websearch_to_tsquery('english'::regconfig, @qt)
                LIMIT 50
            )
            SELECT d.source, d.path, d.kind, d.title, d.scope,
                   ts_headline('english'::regconfig, d.body, websearch_to_tsquery('english'::regconfig, @qt),
                               'MaxFragments=2,MinWords=5,MaxWords=18') AS snippet,
                   (COALESCE(1.0/(60+v.rk),0) + COALESCE(1.0/(60+f.rk),0))::float8 AS rrf
            FROM documents d
            LEFT JOIN v ON v.doc_id = d.doc_id
            LEFT JOIN f ON f.doc_id = d.doc_id
            WHERE v.doc_id IS NOT NULL OR f.doc_id IS NOT NULL
            ORDER BY rrf DESC LIMIT @lim
            """;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("qv", VecLiteral(queryVector));
        cmd.Parameters.AddWithValue("qt", queryText);
        cmd.Parameters.AddWithValue("lim", limit);
        var hits = new List<SearchHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            hits.Add(new SearchHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5), r.GetDouble(6)));
        return hits;
    }
}
