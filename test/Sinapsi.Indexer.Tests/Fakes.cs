// ---------------------------------------------------------------------------
// Fakes - injected test doubles for the store + embedder seams, so the tool
// hardening paths can be proven WITHOUT a live Postgres / ONNX model.
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Npgsql;
using Sinapsi.Indexer;

namespace Sinapsi.Indexer.Tests;

/// <summary>
/// An <see cref="IIndexStore"/> that THROWS the moment any read method is
/// reached. A tool guarded by validation must short-circuit before it ever calls
/// the store, so a test that points a tool at this fake proves the guard fired:
/// if the store were reached, the sentinel exception would surface instead of the
/// expected structured validation error.
/// </summary>
internal sealed class ThrowingIndexStore : IIndexStore
{
    /// <summary>The message the sentinel throws with — asserted-against so a test
    /// can prove the store was NOT reached (the guard fired) vs reached.</summary>
    internal const string Sentinel = "STORE-REACHED-should-have-short-circuited";

    public Task EnsureSchemaAsync(CancellationToken ct) => throw new InvalidOperationException(Sentinel);
    public Task<bool> UpsertAsync(Document doc, CancellationToken ct) => throw new InvalidOperationException(Sentinel);
    public Task<int> TombstoneMissingAsync(string source, IReadOnlyCollection<string> presentDocIds, CancellationToken ct) => throw new InvalidOperationException(Sentinel);
    public Task<int> TombstoneSourcesNotInAsync(IReadOnlyCollection<string> keepSources, CancellationToken ct) => throw new InvalidOperationException(Sentinel);
    public Task PingAsync(CancellationToken ct) => throw new InvalidOperationException(Sentinel);
    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, string? source, string? kind, int limit, CancellationToken ct) => throw new InvalidOperationException(Sentinel);
    public Task<IReadOnlyList<LearningHit>> GetLearningsAsync(string? scope, string? query, int limit, CancellationToken ct) => throw new InvalidOperationException(Sentinel);
    public Task SetEmbeddingAsync(string docId, float[] vector, CancellationToken ct) => throw new InvalidOperationException(Sentinel);
    public Task<IReadOnlyList<(string DocId, string Title, string Body)>> GetMissingEmbeddingsAsync(int limit, CancellationToken ct) => throw new InvalidOperationException(Sentinel);
    public Task<IReadOnlyList<SearchHit>> SemanticSearchAsync(float[] queryVector, string queryText, int limit, CancellationToken ct) => throw new InvalidOperationException(Sentinel);
}

/// <summary>
/// An <see cref="IIndexStore"/> whose read methods throw an exception carrying a
/// fake SECRET in the message — used to prove the tool routes an upstream error
/// through <c>IndexerErrors</c> so the secret is redacted, not echoed verbatim.
/// </summary>
internal sealed class LeakingIndexStore : IIndexStore
{
    /// <summary>The fake secret embedded in the thrown message. It is shaped as a
    /// credential assignment so the sanitizer's SecretAssignment regex matches.</summary>
    internal const string Secret = "hunter2-supersecret";

    private static Exception Leak() =>
        new InvalidOperationException(
            $"connection failed: password={Secret} while connecting to db");

    public Task EnsureSchemaAsync(CancellationToken ct) => throw Leak();
    public Task<bool> UpsertAsync(Document doc, CancellationToken ct) => throw Leak();
    public Task<int> TombstoneMissingAsync(string source, IReadOnlyCollection<string> presentDocIds, CancellationToken ct) => throw Leak();
    public Task<int> TombstoneSourcesNotInAsync(IReadOnlyCollection<string> keepSources, CancellationToken ct) => throw Leak();
    public Task PingAsync(CancellationToken ct) => throw Leak();
    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, string? source, string? kind, int limit, CancellationToken ct) => throw Leak();
    public Task<IReadOnlyList<LearningHit>> GetLearningsAsync(string? scope, string? query, int limit, CancellationToken ct) => throw Leak();
    public Task SetEmbeddingAsync(string docId, float[] vector, CancellationToken ct) => throw Leak();
    public Task<IReadOnlyList<(string DocId, string Title, string Body)>> GetMissingEmbeddingsAsync(int limit, CancellationToken ct) => throw Leak();
    public Task<IReadOnlyList<SearchHit>> SemanticSearchAsync(float[] queryVector, string queryText, int limit, CancellationToken ct) => throw Leak();
}

/// <summary>A no-op embedder that returns a fixed-length zero vector — lets the
/// semantic-search tool run without loading the ONNX model. It must never be
/// reached when validation short-circuits.</summary>
internal sealed class FakeEmbedder : IEmbedder
{
    public int Dim => 384;
    public float[] Embed(string text) => new float[Dim];
}

/// <summary>
/// A recording <see cref="IIndexStore"/> for the worker's per-doc resilience test.
/// <c>UpsertAsync</c> records every DocId it is asked to store, and throws a
/// caller-chosen exception for one poison DocId — letting a test prove that one bad
/// doc is skipped while the rest of the repo's docs are still upserted, and that a
/// data-dependent server error (<see cref="PostgresException"/>) is swallowed while a
/// fatal one (<see cref="NpgsqlException"/>) still propagates. Only the write side is
/// implemented; the read side is unused here.
/// </summary>
internal sealed class RecordingIndexStore : IIndexStore
{
    private readonly string _poisonDocId;
    private readonly Func<Exception> _onPoison;

    /// <summary>DocIds that reached the store successfully (upserted).</summary>
    public List<string> Upserted { get; } = new();
    /// <summary>The source + present-set handed to the tombstone pass (proves it still ran).</summary>
    public string? TombstoneSource { get; private set; }
    public string[]? TombstonePresent { get; private set; }

    public RecordingIndexStore(string poisonDocId, Func<Exception> onPoison)
    {
        _poisonDocId = poisonDocId;
        _onPoison = onPoison;
    }

    public Task<bool> UpsertAsync(Document doc, CancellationToken ct)
    {
        if (doc.DocId == _poisonDocId) throw _onPoison();
        Upserted.Add(doc.DocId);
        return Task.FromResult(true);
    }

    public Task<int> TombstoneMissingAsync(string source, IReadOnlyCollection<string> presentDocIds, CancellationToken ct)
    {
        TombstoneSource = source;
        TombstonePresent = presentDocIds.ToArray();
        return Task.FromResult(0);
    }

    /// <summary>The keepSources handed to the retired-source prune (proves
    /// ReindexAllAsync ran it with the full CONFIGURED source set).</summary>
    public string[]? PrunedKeepSources { get; private set; }

    public Task<int> TombstoneSourcesNotInAsync(IReadOnlyCollection<string> keepSources, CancellationToken ct)
    {
        PrunedKeepSources = keepSources.ToArray();
        return Task.FromResult(0);
    }

    // Unused by the worker path under test — assert non-reach.
    public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
    public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, string? source, string? kind, int limit, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<LearningHit>> GetLearningsAsync(string? scope, string? query, int limit, CancellationToken ct) => throw new NotSupportedException();
    public Task SetEmbeddingAsync(string docId, float[] vector, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<(string DocId, string Title, string Body)>> GetMissingEmbeddingsAsync(int limit, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<SearchHit>> SemanticSearchAsync(float[] queryVector, string queryText, int limit, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>An embedder that throws if reached — proves semantic_search validates
/// BEFORE embedding.</summary>
internal sealed class ThrowingEmbedder : IEmbedder
{
    internal const string Sentinel = "EMBEDDER-REACHED-should-have-short-circuited";
    public int Dim => 384;
    public float[] Embed(string text) => throw new InvalidOperationException(Sentinel);
}
