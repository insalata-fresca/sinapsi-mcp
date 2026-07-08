namespace Cervello.Enrichment.Ports;

/// <summary>
/// A live-Postgres adapter that owns one or more tables and can create them idempotently on
/// startup. Every Pg* store implements this so the host can ensure ALL adapter schemas — not just
/// the ledger — before the drain worker runs. (Regression guard: MIGRATE-FIX — the host used to
/// ensure only <c>enrichment_ledger</c>, so <c>correction_map</c> / <c>voiceprints</c> /
/// <c>open_points</c> were never created on a fresh CT146, and the CorrectionStage failed with
/// <c>42P01 relation "correction_map" does not exist</c>.)
/// </summary>
public interface ISchemaInitializer
{
    /// <summary>
    /// A human-readable name for the schema this initializer owns (the table, or a comma-joined
    /// list for a multi-table adapter). Used only for startup logging — NOT a wire contract.
    /// </summary>
    string SchemaName { get; }

    /// <summary>
    /// Create this adapter's table(s) if absent. MUST be idempotent (<c>CREATE TABLE IF NOT
    /// EXISTS</c>) and safe to call on every boot; MAY retry transient connection failures
    /// internally, but a hard/persistent error MUST propagate so startup fails closed.
    /// </summary>
    Task EnsureSchemaAsync(CancellationToken ct = default);
}
