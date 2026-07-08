using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the open-points queue (DESIGN §6.1; design Data Model → <c>open_points</c>). The apply
/// stage ENQUEUES an escalated fact here rather than writing it to <c>map/</c>; the cervello MCP
/// (E5) lists + answers them. CT146 Postgres in prod, in-memory in tests. Enqueue is idempotent on
/// <c>point_id</c> (a re-run of apply re-enqueues the same point as a no-op). Resolution is
/// idempotent too — a resolved point cannot be re-resolved (prevents a double-apply).
/// </summary>
public interface IOpenPointStore
{
    /// <summary>Enqueue an open-point (idempotent on point id). Returns true if newly added.</summary>
    Task<bool> EnqueueAsync(OpenPoint point, CancellationToken ct = default);

    /// <summary>The currently PENDING (unresolved) open-points, optionally filtered by recording.</summary>
    Task<IReadOnlyList<OpenPoint>> ListPendingAsync(string? recordingId = null, CancellationToken ct = default);

    /// <summary>Get an open-point by id (pending or resolved), or null if unknown.</summary>
    Task<OpenPoint?> GetAsync(string pointId, CancellationToken ct = default);

    /// <summary>
    /// Mark a point resolved with the operator's <c>human://&lt;answer-id&gt;</c> basis (or a
    /// dismissal). Idempotent + single-shot: returns <c>false</c> if the point is unknown OR was
    /// ALREADY resolved (the double-apply guard); <c>true</c> on the first resolution only.
    /// </summary>
    Task<bool> ResolveAsync(string pointId, OpenPointResolution resolution, CancellationToken ct = default);

    /// <summary>Whether a point has already been resolved (or dismissed).</summary>
    Task<bool> IsResolvedAsync(string pointId, CancellationToken ct = default);
}

/// <summary>The recorded resolution of an open-point (an operator answer or a dismissal).</summary>
public sealed record OpenPointResolution
{
    private OpenPointResolution(bool dismissed, string? confirmedValue, string? basisId, string answerId)
    {
        Dismissed = dismissed;
        ConfirmedValue = confirmedValue;
        BasisId = basisId;
        AnswerId = answerId;
    }

    /// <summary>Whether the point was dismissed (fact omitted, never guessed) vs. answered.</summary>
    public bool Dismissed { get; }

    /// <summary>The confirmed value for an answered (non-dismissed) resolution; null for a dismissal.</summary>
    public string? ConfirmedValue { get; }

    /// <summary>The <c>human://&lt;answer-id&gt;</c> basis id for an answered resolution; null for a dismissal.</summary>
    public string? BasisId { get; }

    /// <summary>The answer id (<c>op_…</c> / the same id used to form <see cref="BasisId"/>).</summary>
    public string AnswerId { get; }

    public static OpenPointResolution Answered(string confirmedValue, string basisId, string answerId) =>
        new(dismissed: false, confirmedValue, basisId, answerId);

    public static OpenPointResolution DismissedBy(string answerId) =>
        new(dismissed: true, confirmedValue: null, basisId: null, answerId);
}
