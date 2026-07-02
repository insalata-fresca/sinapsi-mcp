// ---------------------------------------------------------------------------
// IndexerConfigTests - fail-closed numeric config: a valid value binds; an
// unset var takes the default; a non-numeric / out-of-range value throws naming
// the offending env var.
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

/// <summary>
/// Pins <see cref="IndexerConfig"/>. These mutate process env, so they run
/// serially in the "indexer-env" collection. The matrix proves the fail-closed
/// contract on every numeric knob (default when unset, accept in-range, THROW
/// naming the var on garbage / floor-violation / ceiling-violation).
/// </summary>
[Collection("indexer-env")]
public sealed class IndexerConfigTests : IDisposable
{
    private static readonly string[] Vars =
    {
        "INDEXER_RESCAN_INTERVAL_MIN", "INDEXER_DEBOUNCE_SEC",
        "INDEXER_EMBED_IDLE_SEC", "INDEXER_EMBED_THROTTLE_MS",
        "INDEXER_HEALTH_PORT", "EMBED_MAX_TOKENS", "EMBED_DIM",
    };

    public IndexerConfigTests() => ClearAll();
    public void Dispose() => ClearAll();
    private static void ClearAll() { foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null); }

    // ── defaults (unset) ──────────────────────────────────────────────────────

    [Fact]
    public void Defaults_AreTakenWhenUnset()
    {
        Assert.Equal(IndexerConfig.DefaultRescanIntervalMin, IndexerConfig.RescanIntervalMin());
        Assert.Equal(IndexerConfig.DefaultDebounceSec, IndexerConfig.DebounceSec());
        Assert.Equal(IndexerConfig.DefaultEmbedIdleSec, IndexerConfig.EmbedIdleSec());
        Assert.Equal(IndexerConfig.DefaultEmbedThrottleMs, IndexerConfig.EmbedThrottleMs());
        Assert.Equal(IndexerConfig.DefaultHealthPort, IndexerConfig.HealthPort());
        Assert.Equal(IndexerConfig.DefaultEmbedMaxTokens, IndexerConfig.EmbedMaxTokens());
        Assert.Equal(IndexerConfig.DefaultEmbedDim, IndexerConfig.EmbedDim());
    }

    // ── valid in-range value binds ────────────────────────────────────────────

    [Fact]
    public void InRangeValue_Binds()
    {
        Environment.SetEnvironmentVariable("INDEXER_DEBOUNCE_SEC", "42");
        Assert.Equal(42, IndexerConfig.DebounceSec());

        Environment.SetEnvironmentVariable("INDEXER_HEALTH_PORT", "9109");
        Assert.Equal(9109, IndexerConfig.HealthPort());
    }

    // ── throttle floor is 0 (a legit value) ──────────────────────────────────

    [Fact]
    public void EmbedThrottle_AcceptsZero_TheFloor()
    {
        Environment.SetEnvironmentVariable("INDEXER_EMBED_THROTTLE_MS", "0");
        Assert.Equal(0, IndexerConfig.EmbedThrottleMs());
    }

    // ── non-numeric is rejected naming the var ────────────────────────────────

    [Theory]
    [InlineData("INDEXER_RESCAN_INTERVAL_MIN")]
    [InlineData("INDEXER_DEBOUNCE_SEC")]
    [InlineData("INDEXER_HEALTH_PORT")]
    [InlineData("EMBED_DIM")]
    public void NonNumeric_Throws_NamingTheVar(string var)
    {
        Environment.SetEnvironmentVariable(var, "not-a-number");
        var ex = Assert.Throws<InvalidOperationException>(() => Read(var));
        Assert.Contains(var, ex.Message);
    }

    // ── below floor / above ceiling is rejected naming the var ────────────────

    [Fact]
    public void BelowFloor_Throws_NamingTheVar()
    {
        // Debounce floor is 2; 1 is below it.
        Environment.SetEnvironmentVariable("INDEXER_DEBOUNCE_SEC", "1");
        var ex = Assert.Throws<InvalidOperationException>(() => IndexerConfig.DebounceSec());
        Assert.Contains("INDEXER_DEBOUNCE_SEC", ex.Message);
    }

    [Fact]
    public void ZeroPort_Throws_NamingTheVar()
    {
        Environment.SetEnvironmentVariable("INDEXER_HEALTH_PORT", "0");
        var ex = Assert.Throws<InvalidOperationException>(() => IndexerConfig.HealthPort());
        Assert.Contains("INDEXER_HEALTH_PORT", ex.Message);
    }

    [Fact]
    public void PortAboveCeiling_Throws()
    {
        Environment.SetEnvironmentVariable("INDEXER_HEALTH_PORT", "70000");
        Assert.Throws<InvalidOperationException>(() => IndexerConfig.HealthPort());
    }

    [Fact]
    public void RescanAboveCeiling_Throws_NamingTheVar()
    {
        Environment.SetEnvironmentVariable(
            "INDEXER_RESCAN_INTERVAL_MIN",
            (IndexerConfig.MaxRescanIntervalMin + 1).ToString());
        var ex = Assert.Throws<InvalidOperationException>(() => IndexerConfig.RescanIntervalMin());
        Assert.Contains("INDEXER_RESCAN_INTERVAL_MIN", ex.Message);
    }

    // Dispatch a read for the given var by name (used by the non-numeric matrix).
    private static int Read(string var) => var switch
    {
        "INDEXER_RESCAN_INTERVAL_MIN" => IndexerConfig.RescanIntervalMin(),
        "INDEXER_DEBOUNCE_SEC" => IndexerConfig.DebounceSec(),
        "INDEXER_EMBED_IDLE_SEC" => IndexerConfig.EmbedIdleSec(),
        "INDEXER_EMBED_THROTTLE_MS" => IndexerConfig.EmbedThrottleMs(),
        "INDEXER_HEALTH_PORT" => IndexerConfig.HealthPort(),
        "EMBED_MAX_TOKENS" => IndexerConfig.EmbedMaxTokens(),
        "EMBED_DIM" => IndexerConfig.EmbedDim(),
        _ => throw new ArgumentOutOfRangeException(nameof(var)),
    };
}
