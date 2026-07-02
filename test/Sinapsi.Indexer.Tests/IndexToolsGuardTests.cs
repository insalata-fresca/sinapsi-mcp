// ---------------------------------------------------------------------------
// IndexToolsGuardTests - the LOAD-BEARING hardening leg for the read tools
// (mirrors StepCa's SubprocessToolErrorTests):
//   1. invalid input -> structured error, with the store/embedder NOT reached
//      (the fakes throw a sentinel if reached, proving the short-circuit);
//   2. a store that leaks a secret in its exception -> the tool returns
//      [redacted], never the raw secret (the Sanitize path fires end-to-end);
//   3. a valid input reaches the store (proves the guard is not over-eager).
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

public sealed class IndexToolsGuardTests
{
    // ── search_index: validation short-circuits BEFORE the store ─────────────

    [Theory]
    [InlineData("", null, null, 10)]              // empty query
    [InlineData("ok", null, "bogus-kind", 10)]    // unknown kind
    [InlineData("ok", null, null, 0)]             // non-positive limit
    [InlineData("ok", null, null, 999)]           // absurd limit
    [InlineData("bad\0query", null, null, 10)]    // control char (\0 escape, never a literal NUL)
    public async Task SearchIndex_InvalidInput_ShortCircuitsBeforeStore(
        string query, string? source, string? kind, int limit)
    {
        // ThrowingIndexStore throws its sentinel the instant it is reached; a
        // clean structured-error return proves the guard fired first.
        var r = await IndexTools.SearchIndex(new ThrowingIndexStore(), query, source, kind, limit);

        Assert.True(Anon.HasError(r));
        Assert.DoesNotContain(ThrowingIndexStore.Sentinel, Anon.Error(r));
    }

    [Fact]
    public async Task SearchIndex_ValidInput_ReachesStore_AndErrorIsScrubbed()
    {
        // A valid input clears validation, so the (leaking) store IS reached; its
        // secret-bearing exception must come back redacted, not raw.
        var r = await IndexTools.SearchIndex(new LeakingIndexStore(), query: "hybrid search");

        Assert.True(Anon.HasError(r));
        var err = Anon.Error(r)!;
        Assert.DoesNotContain(LeakingIndexStore.Secret, err);
        Assert.Contains("[redacted]", err);
    }

    // ── semantic_search: validates BEFORE embedding AND before the store ─────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SemanticSearch_EmptyQuery_KeepsExactParityMessage(string query)
    {
        // Parity: the empty-query message is exactly "query is required" and the
        // embedder is never reached.
        var r = await IndexTools.SemanticSearch(new ThrowingIndexStore(), new ThrowingEmbedder(), query);
        Assert.Equal("query is required", Anon.Error(r));
    }

    [Fact]
    public async Task SemanticSearch_BadLimit_ShortCircuitsBeforeEmbedderAndStore()
    {
        var r = await IndexTools.SemanticSearch(new ThrowingIndexStore(), new ThrowingEmbedder(), query: "ok", limit: -5);
        Assert.True(Anon.HasError(r));
        Assert.DoesNotContain(ThrowingEmbedder.Sentinel, Anon.Error(r));
        Assert.DoesNotContain(ThrowingIndexStore.Sentinel, Anon.Error(r));
    }

    [Fact]
    public async Task SemanticSearch_ValidInput_ReachesStore_AndErrorIsScrubbed()
    {
        // FakeEmbedder returns a zero vector (no ONNX model needed); the leaking
        // store then throws a secret-bearing exception which must be redacted.
        var r = await IndexTools.SemanticSearch(new LeakingIndexStore(), new FakeEmbedder(), query: "meaning query");
        Assert.True(Anon.HasError(r));
        Assert.DoesNotContain(LeakingIndexStore.Secret, Anon.Error(r));
        Assert.Contains("[redacted]", Anon.Error(r));
    }

    // ── get_learning: optional scope+query still validated ───────────────────

    [Theory]
    [InlineData("bad\nscope", null, 10)]          // control char in scope
    [InlineData(null, "bad\0query", 10)]          // control char in optional query
    [InlineData(null, null, 0)]                    // bad limit
    public async Task GetLearning_InvalidInput_ShortCircuitsBeforeStore(string? scope, string? query, int limit)
    {
        var r = await IndexTools.GetLearning(new ThrowingIndexStore(), scope, query, limit);
        Assert.True(Anon.HasError(r));
        Assert.DoesNotContain(ThrowingIndexStore.Sentinel, Anon.Error(r));
    }

    [Fact]
    public async Task GetLearning_ValidInput_ReachesStore_AndErrorIsScrubbed()
    {
        var r = await IndexTools.GetLearning(new LeakingIndexStore(), scope: "global", query: null);
        Assert.True(Anon.HasError(r));
        Assert.DoesNotContain(LeakingIndexStore.Secret, Anon.Error(r));
        Assert.Contains("[redacted]", Anon.Error(r));
    }
}
