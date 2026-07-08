namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the per-caller DELTA BASELINE cursor (design §2.6, MC-ratified Q9: server-side, per-caller).
/// A <c>goal_reasoning</c> / <c>portfolio</c> pack carries a <c>delta</c> block of "what changed since
/// I last looked", diffed against a baseline the SERVER holds — reusing the exact mechanism the bridge
/// already has for <c>list_recent_additions</c>: a cursor stored server-side, keyed by the caller's
/// identity (<c>jwt:{sub}</c> or a hash of the bearer). CT146 reads the last-sweep <c>as_of</c>, the
/// assembler diffs against it, then ADVANCES the baseline to the new <c>as_of</c>.
///
/// <para>Live = the CT146 Postgres <c>pack_delta_cursor</c> table; fake = in-memory (tests / offline).
/// The key is opaque and content-free — no personal data, only an identity hash + a timestamp.</para>
/// </summary>
public interface IDeltaCursorStore
{
    /// <summary>The caller's last-sweep baseline <c>as_of</c> for this intent, or null if never swept.</summary>
    Task<string?> GetBaselineAsync(string callerKey, string intent, CancellationToken ct = default);

    /// <summary>Advance the baseline to <paramref name="asOf"/> after a delta-bearing pack was served.</summary>
    Task AdvanceAsync(string callerKey, string intent, string asOf, CancellationToken ct = default);
}
