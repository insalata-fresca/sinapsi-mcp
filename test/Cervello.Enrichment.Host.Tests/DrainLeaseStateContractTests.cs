using Cervello.Enrichment.Host.Drain;
using Xunit;

namespace Cervello.Enrichment.Host.Tests;

/// <summary>
/// Pins the WATCHER-WRITE ↔ DRAIN-READ state contract on the shared <c>watcher_recording</c> row.
///
/// The Watcher persists a normalized recording's <c>state</c> column as the SCHEMAS §5 wire name
/// <c>normalized</c> (<c>Cervello.Watcher.Domain.PipelineState.Normalized.ToWire()</c>, E4). A
/// PRE-E4 Watcher build persisted the PascalCase <c>Enum.ToString()</c> form <c>Normalized</c>, and
/// the Watcher's own <c>PipelineStateWire.Parse</c> is deliberately tolerant of BOTH so legacy rows
/// load without a data migration.
///
/// <para>The drain reads that row with a RAW SQL literal (<see cref="PgNormalizedWorkQueue.LeaseSql"/>),
/// which never passes through <c>Parse</c>. Postgres string equality is CASE-SENSITIVE, so if the
/// drain matched only <c>'normalized'</c> it would silently skip every legacy <c>Normalized</c> row —
/// the exact stall behind the 2026-07-08 handoff incident (3 real recordings stuck at <c>Normalized</c>
/// while the drain leased 0, masked because the synthetic verify inserted the lowercase form). These
/// tests fail if the drain's lease predicate ever again fails to accept BOTH forms the Watcher can
/// write — so a fake / synthetic-only row can no longer hide the divergence.</para>
/// </summary>
public sealed class DrainLeaseStateContractTests
{
    // The two forms the Watcher can persist for the "ready to enrich" terminal:
    //   - `normalized`  = PipelineState.Normalized.ToWire()      (post-E4, the canonical §5 wire name)
    //   - `Normalized`  = PipelineState.Normalized.ToString()    (legacy pre-E4 PascalCase; must still drain)
    private const string WireForm = "normalized";
    private const string LegacyPascalForm = "Normalized";

    [Fact]
    public void Lease_predicate_accepts_the_canonical_wire_state()
    {
        Assert.Contains($"'{WireForm}'", PgNormalizedWorkQueue.LeaseSql);
    }

    [Fact]
    public void Lease_predicate_also_accepts_the_legacy_pascalcase_state_no_migration()
    {
        // If this fails, a stale/pre-E4 Watcher image's rows would strand forever (case-sensitive SQL).
        Assert.Contains($"'{LegacyPascalForm}'", PgNormalizedWorkQueue.LeaseSql);
    }

    [Fact]
    public void Lease_predicate_filters_on_the_state_column_only_by_the_normalized_forms()
    {
        // Guard the intent: the WHERE clause targets exactly the two `normalized` forms and nothing
        // downstream (no `enriched`, `graph_pr_opened`, … re-leasing already-advanced rows).
        var sql = PgNormalizedWorkQueue.LeaseSql;
        Assert.Contains("WHERE r.state IN ('normalized', 'Normalized')", sql);
        Assert.DoesNotContain("enriched", sql);
        Assert.DoesNotContain("graph_pr_opened", sql);
    }
}
