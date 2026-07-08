using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for opening the back-linked <c>map/</c> review-PR (DESIGN §5; the deliberately-separate
/// <c>cervello-graph-writer</c>). The live adapter authors a branch + PR against <c>ste/cervello</c>
/// (where <c>cervello-lint</c> re-runs as the pre-merge check); a fake captures the assembled PR in
/// tests so the writer's own R1/R4/R5/R11 self-check is exercised offline (no network, no deploy).
/// The PR is NEVER auto-merged — a human gate merges it (like the UI Factory).
/// </summary>
public interface IMapPrWriter
{
    /// <summary>Open a review-PR for a fully-assembled, self-linted mutation set. Returns the PR handle.</summary>
    Task<MapPrHandle> OpenPrAsync(MapReviewPr pr, CancellationToken ct = default);
}

/// <summary>The handle to an opened review-PR (branch + a stable ref for logging / bundle linkage).</summary>
public sealed record MapPrHandle(string Branch, string Title, int? Number = null);
