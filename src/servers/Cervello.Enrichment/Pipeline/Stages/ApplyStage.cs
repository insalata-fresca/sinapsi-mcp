using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline.Stages;

/// <summary>
/// The apply stage / GRAPH-ADD (spec <c>enrichment-linking</c> → "Apply via GRAPH-ADD under the
/// decision policy"; DESIGN §5 / §5.2 step 5). It routes every already-graded proposed fact
/// (attribution verdict, correction verdict, proposed timeline line) to exactly one destination:
///
/// <list type="bullet">
/// <item><b>auto_applied / flagged</b> → a <see cref="MapMutation"/> carrying source + confidence +
///   basis, back-linked to the bundle; all collected into ONE back-linked review-PR via
///   <see cref="CervelloGraphWriter"/> (lint R1/R4/R5/R11).</item>
/// <item><b>open_point</b> → enqueued in <see cref="IOpenPointStore"/> (DESIGN §6.1); NO map
///   mutation is written until the operator answers.</item>
/// <item><b>omitted</b> → dropped, the gap recorded (never guessed).</item>
/// </list>
///
/// <para>The escalate-only phase gate is already baked into the verdicts by the policy/grader —
/// in the first concept everything applied is downgraded to <c>open_point</c>, so the apply stage
/// opens NO map PR and only fills the open-points queue. The apply stage never re-decides; it only
/// routes what the policy already ruled.</para>
/// </summary>
public sealed class ApplyStage(
    CervelloGraphWriter graphWriter,
    IOpenPointStore openPoints,
    ILogger<ApplyStage>? logger = null)
{
    private readonly CervelloGraphWriter _graph = graphWriter ?? throw new ArgumentNullException(nameof(graphWriter));
    private readonly IOpenPointStore _openPoints = openPoints ?? throw new ArgumentNullException(nameof(openPoints));
    private readonly ILogger _log = logger ?? NullLogger<ApplyStage>.Instance;

    /// <summary>Route all graded facts for a bundle: applied → map-PR, escalated → queue, omitted → drop.</summary>
    public async Task<ApplyResult> ApplyAsync(ApplyInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var mutations = new List<MapMutation>();
        var referenced = new List<ReferencedLink>();
        var enqueued = 0;
        var omitted = 0;

        // ── attributions ────────────────────────────────────────────────────────────────────────
        foreach (var v in input.Attributions)
        {
            switch (v.Outcome)
            {
                case AttributionOutcome.AutoApplied:
                case AttributionOutcome.Flagged:
                    // An applied attribution → a dossier note carrying source + confidence + basis (R9).
                    mutations.Add(new MapMutation(
                        dossierPath: $"map/people/{v.Person}.md",
                        section: "## Timeline",
                        content: $"- {input.Date} — spoke in {input.RecordingId} ({v.MergedSpeaker}) — source: {v.SourceRef}",
                        source: v.SourceRef!,
                        confidence: v.Confidence,
                        bundleId: input.BundleId,
                        basisId: v.Basis!.Id));
                    referenced.Add(new ReferencedLink(v.Person!, "person"));
                    break;

                case AttributionOutcome.OpenPoint:
                    enqueued += await EnqueueAsync(OpenPointKind.Speaker, input,
                        $"which enrolled person is {v.MergedSpeaker}?", v.Person is null ? [] : [v.Person], ct);
                    break;

                case AttributionOutcome.Omitted:
                    omitted++; // unidentified — dropped, gap recorded (never guessed)
                    _log.LogInformation("apply {Bundle}: attribution {Speaker} omitted ({Reason})",
                        input.BundleId, v.MergedSpeaker, v.Reason);
                    break;
            }
        }

        // ── corrections ─────────────────────────────────────────────────────────────────────────
        foreach (var c in input.Corrections)
        {
            switch (c.Outcome)
            {
                case CorrectionOutcome.AutoApplied:
                case CorrectionOutcome.Flagged:
                    // A correction diff does not itself mutate the map (it edits the transcript diff
                    // set); it is recorded on the recording's attribution/transcript, not map/. We
                    // only surface map-relevant corrections that carry a map ref. Here corrections
                    // are transcript-scoped, so they are NOT map mutations — logged as applied.
                    _log.LogInformation("apply {Bundle}: correction diff applied to transcript ({Ref})",
                        input.BundleId, c.Diff!.EvidenceRef);
                    break;

                case CorrectionOutcome.OpenPoint:
                    enqueued += await EnqueueAsync(OpenPointKind.Correction, input,
                        "which correction is right?", c.Candidates, ct);
                    break;

                case CorrectionOutcome.Omitted:
                    omitted++;
                    break;
            }
        }

        // ── proposed timeline / link facts (already graded to applied here) ──────────────────────
        foreach (var m in input.TimelineMutations)
        {
            mutations.Add(m);
            foreach (var l in input.ReferencedLinks) referenced.Add(l);
        }

        // Open ONE back-linked review-PR for all applied mutations (or none if empty / escalate-only).
        MapPrHandle? pr = null;
        if (mutations.Count > 0)
        {
            var request = new GraphAddRequest(input.BundleId, mutations,
                referenced.DistinctBy(r => r.Slug, StringComparer.Ordinal).ToList());
            pr = await _graph.OpenReviewPrAsync(request, ct).ConfigureAwait(false);
        }

        _log.LogInformation("apply {Bundle}: {Mut} mutations→PR, {Open} open-points, {Omit} omitted",
            input.BundleId, mutations.Count, enqueued, omitted);
        return new ApplyResult(pr, mutations.Count, enqueued, omitted);
    }

    private async Task<int> EnqueueAsync(
        OpenPointKind kind, ApplyInput input, string question, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        var pointId = $"{input.BundleId}:{kind}:{question.GetHashCode():x8}";
        var added = await _openPoints.EnqueueAsync(
            new OpenPoint(pointId, kind, input.RecordingId, input.BundleId, question, candidates), ct)
            .ConfigureAwait(false);
        return added ? 1 : 0;
    }
}

/// <summary>The graded facts + context the apply stage routes.</summary>
public sealed record ApplyInput
{
    public ApplyInput(
        string bundleId,
        string recordingId,
        string date,
        IReadOnlyList<AttributionVerdict> attributions,
        IReadOnlyList<CorrectionVerdict> corrections,
        IReadOnlyList<MapMutation> timelineMutations,
        IReadOnlyList<ReferencedLink> referencedLinks)
    {
        if (string.IsNullOrWhiteSpace(bundleId))
            throw new ArgumentException("ApplyInput.BundleId must be non-empty", nameof(bundleId));
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("ApplyInput.RecordingId must be non-empty", nameof(recordingId));
        BundleId = bundleId;
        RecordingId = recordingId;
        Date = string.IsNullOrWhiteSpace(date) ? "" : date;
        Attributions = attributions ?? Array.Empty<AttributionVerdict>();
        Corrections = corrections ?? Array.Empty<CorrectionVerdict>();
        TimelineMutations = timelineMutations ?? Array.Empty<MapMutation>();
        ReferencedLinks = referencedLinks ?? Array.Empty<ReferencedLink>();
    }

    public string BundleId { get; }
    public string RecordingId { get; }
    public string Date { get; }
    public IReadOnlyList<AttributionVerdict> Attributions { get; }
    public IReadOnlyList<CorrectionVerdict> Corrections { get; }
    public IReadOnlyList<MapMutation> TimelineMutations { get; }
    public IReadOnlyList<ReferencedLink> ReferencedLinks { get; }
}

/// <summary>The outcome of an apply pass.</summary>
public sealed record ApplyResult(MapPrHandle? Pr, int Mutations, int OpenPoints, int Omitted);
