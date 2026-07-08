using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the DERIVED, non-attribution facts a recording's enrichment needs but that no single
/// merged stage owns: the summary/entities/dates, the proposed <c>[[link]]</c>s + timeline lines,
/// the attention verdict, the resolved-participant set (for correction grounding), and the garbled
/// spans (the selective-re-ASR candidates). In a live deployment these come from the attention
/// scorer + the transcript + the manifest/org-chart; here they are one seam so the orchestrator can
/// thread a recording end-to-end against a fake, WITHOUT any stage inventing them.
///
/// <para><b>Grounding.</b> Every fact this source yields is later re-gated by a stage: proposed
/// timeline lines are R1-checked by <see cref="ProposedTimelineLine"/>'s ctor; the correction diffs
/// derived from <see cref="RecordingFacts.Participants"/> are evidence-gated by the grader; the
/// attribution is computed by the attribution stage from voiceprints (NOT taken from here). This
/// source never asserts an identity or an unsourced fact — it supplies the substrate the grounded
/// stages then admit or reject.</para>
/// </summary>
public interface IRecordingFactSource
{
    /// <summary>
    /// The derived facts for a recording (summary/entities/dates/links/timeline/attention/
    /// participants/garbled-spans). The base transcript is passed so a live adapter can derive
    /// facts from it; a fake returns scripted facts.
    /// </summary>
    Task<RecordingFacts> GetFactsAsync(
        string recordingId, BaseTranscript baseTranscript, CancellationToken ct = default);
}

/// <summary>
/// The derived, non-attribution enrichment facts for one recording (see
/// <see cref="IRecordingFactSource"/>). The attribution entries are computed by the attribution
/// stage from voiceprints — they are NOT part of this record.
/// </summary>
public sealed record RecordingFacts
{
    public RecordingFacts(
        string summary,
        IReadOnlyList<string> entities,
        IReadOnlyList<string> dates,
        IReadOnlyList<ProposedLink> proposedLinks,
        IReadOnlyList<ProposedTimelineLine> proposedTimeline,
        BundleAttention attention,
        IReadOnlyList<ResolvedParticipant> participants,
        IReadOnlyList<TextSpan> garbledSpans)
    {
        ArgumentNullException.ThrowIfNull(attention);
        Summary = summary ?? "";
        Entities = entities ?? Array.Empty<string>();
        Dates = dates ?? Array.Empty<string>();
        ProposedLinks = proposedLinks ?? Array.Empty<ProposedLink>();
        ProposedTimeline = proposedTimeline ?? Array.Empty<ProposedTimelineLine>();
        Attention = attention;
        Participants = participants ?? Array.Empty<ResolvedParticipant>();
        GarbledSpans = garbledSpans ?? Array.Empty<TextSpan>();
    }

    public string Summary { get; }
    public IReadOnlyList<string> Entities { get; }
    public IReadOnlyList<string> Dates { get; }
    public IReadOnlyList<ProposedLink> ProposedLinks { get; }
    public IReadOnlyList<ProposedTimelineLine> ProposedTimeline { get; }
    public BundleAttention Attention { get; }

    /// <summary>The resolved participants that ground the correction stage (aliases → canonical).</summary>
    public IReadOnlyList<ResolvedParticipant> Participants { get; }

    /// <summary>Spans the base transcript marks garbled — the ONLY spans selective re-ASR may run on.</summary>
    public IReadOnlyList<TextSpan> GarbledSpans { get; }
}
