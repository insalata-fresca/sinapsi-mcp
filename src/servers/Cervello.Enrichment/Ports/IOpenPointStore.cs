using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the open-points queue (DESIGN §6.1; design Data Model → <c>open_points</c>). The apply
/// stage ENQUEUES an escalated fact here rather than writing it to <c>map/</c>; the cervello MCP
/// (E5) lists + answers them. CT146 Postgres in prod, in-memory in tests. Enqueue is idempotent on
/// <c>point_id</c> (a re-run of apply re-enqueues the same point as a no-op).
/// </summary>
public interface IOpenPointStore
{
    /// <summary>Enqueue an open-point (idempotent on point id). Returns true if newly added.</summary>
    Task<bool> EnqueueAsync(OpenPoint point, CancellationToken ct = default);

    /// <summary>The currently pending open-points (optionally filtered by recording).</summary>
    Task<IReadOnlyList<OpenPoint>> ListPendingAsync(string? recordingId = null, CancellationToken ct = default);
}
