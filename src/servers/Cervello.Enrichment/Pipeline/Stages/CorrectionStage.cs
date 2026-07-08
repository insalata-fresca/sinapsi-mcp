using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline.Stages;

/// <summary>
/// The grounded text-correction stage (spec <c>text-correction</c>; DESIGN §5.2 step 3). Over the
/// Google/CT126 BASE transcript it runs a context LLM pass (<see cref="ICorrectionLlm"/>) using the
/// historized correction map/glossary + resolved participants, emitting <see cref="CorrectionDiff"/>s
/// — NEVER a wholesale rewrite. Every diff is EVIDENCE-GATED by <see cref="CorrectionGrader"/>:
/// backed by a glossary/participant/re-ASR match → applied; two plausible resolutions → open-point;
/// unbacked → OMITTED (the base text is left as-is, never guessed). Selective re-ASR
/// (<see cref="IReAsrClient"/>) runs ONLY for spans the base marks garbled.
///
/// <para><b>The hard floor (headline invariant):</b> the stage NEVER emits an unbacked correction
/// and NEVER invents text. Its output is the base transcript plus a reviewable list of gated
/// diffs; an omit changes nothing.</para>
///
/// <para><b>Selective re-ASR is OPTIONAL + graceful-degrade (default OFF).</b> CT126 re-ASR is a
/// LATER quality enhancement, not a drain dependency. When it is disabled (the default, or when no
/// re-ASR client is wired), a garbled-span correction has no sanctioned evidence path and is OMITTED
/// — the base text is left as-is (never guessed), and the drain completes without CT126. Even when
/// enabled, a re-ASR call that fails (CT126 unreachable / transient) is caught and the span is
/// OMITTED, never propagated — a garbled span is a quality gap to leave alone, never a reason to fail
/// the whole drain.</para>
/// </summary>
public sealed class CorrectionStage(
    ICorrectionLlm llm,
    ICorrectionMapStore correctionMap,
    IReAsrClient? reAsr,
    CorrectionGrader grader,
    bool reAsrEnabled = false,
    ILogger<CorrectionStage>? logger = null)
{
    private readonly ICorrectionLlm _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    private readonly ICorrectionMapStore _map = correctionMap ?? throw new ArgumentNullException(nameof(correctionMap));
    private readonly IReAsrClient? _reAsr = reAsr;
    private readonly CorrectionGrader _grader = grader ?? throw new ArgumentNullException(nameof(grader));
    // Re-ASR is active only when explicitly enabled AND a client is wired (graceful-degrade default).
    private readonly bool _reAsrEnabled = reAsrEnabled && reAsr is not null;
    private readonly ILogger _log = logger ?? NullLogger<CorrectionStage>.Instance;

    /// <summary>
    /// Run the correction pass over a recording's base transcript.
    /// </summary>
    /// <param name="recordingId">The recording id (for re-ASR span refs).</param>
    /// <param name="baseText">The immutable base transcript text (never overwritten).</param>
    /// <param name="participants">The resolved participants for this recording.</param>
    /// <param name="garbledSpans">Spans the base marks low-confidence/garbled (re-ASR candidates).</param>
    public async Task<CorrectionResult> CorrectAsync(
        string recordingId,
        string baseText,
        IReadOnlyList<ResolvedParticipant> participants,
        IReadOnlyList<TextSpan> garbledSpans,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        ArgumentNullException.ThrowIfNull(baseText);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(garbledSpans);

        var glossary = await _map.GetGlossaryAsync(ct).ConfigureAwait(false);
        var context = new CorrectionContext(glossary, participants);
        var garbled = new HashSet<(int, int)>(garbledSpans.Select(s => (s.Start, s.End)));

        var candidates = await _llm.ProposeAsync(baseText, context, ct).ConfigureAwait(false);

        var diffs = new List<CorrectionDiff>();
        var openPoints = new List<CorrectionVerdict>();
        var omitted = new List<CorrectionVerdict>();

        foreach (var candidate in candidates)
        {
            // Selective re-ASR: invoked ONLY for a garbled-kind span the base flagged garbled.
            ReAsrResult? reAsr = null;
            if (candidate.Kind == CorrectionKind.Garbled)
            {
                if (!garbled.Contains((candidate.Span.Start, candidate.Span.End)))
                {
                    // A garbled-correction proposed for a span the base did NOT mark garbled has no
                    // sanctioned evidence path → omit (never re-ASR the whole transcript / guess).
                    omitted.Add(CorrectionVerdict.Omitted(candidate.Span,
                        $"garbled-correction proposed for a span not marked garbled — re-ASR not invoked (selective only)"));
                    continue;
                }

                if (!_reAsrEnabled)
                {
                    // Re-ASR is a LATER, OPTIONAL enhancement (default OFF / CT126 not a drain dep):
                    // with no re-ASR evidence the garbled span has no sanctioned backing → omit (leave
                    // the base text as-is, never guess). The drain completes without CT126.
                    omitted.Add(CorrectionVerdict.Omitted(candidate.Span,
                        "garbled span left as-is — selective re-ASR disabled (CT126 not required for the drain)"));
                    _log.LogInformation("correction {Rec} [{Start},{End}): re-ASR disabled → span omitted (left as-is)",
                        recordingId, candidate.Span.Start, candidate.Span.End);
                    continue;
                }

                try
                {
                    reAsr = await _reAsr!.ReAsrAsync(recordingId, candidate.Span, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // CT126 unreachable / transient re-ASR failure → GRACEFULLY skip this span (omit,
                    // leave as-is), never fail the drain. Re-ASR is a quality enhancement, not a gate.
                    omitted.Add(CorrectionVerdict.Omitted(candidate.Span,
                        $"garbled span left as-is — re-ASR unavailable ({ex.Message})"));
                    _log.LogWarning(ex, "correction {Rec} [{Start},{End}): re-ASR unavailable → span omitted (drain continues)",
                        recordingId, candidate.Span.Start, candidate.Span.End);
                    continue;
                }
            }

            var verdict = _grader.Grade(candidate, context, reAsr);
            switch (verdict.Outcome)
            {
                case CorrectionOutcome.AutoApplied:
                case CorrectionOutcome.Flagged:
                    diffs.Add(verdict.Diff!);
                    break;
                case CorrectionOutcome.OpenPoint:
                    openPoints.Add(verdict);
                    break;
                case CorrectionOutcome.Omitted:
                    omitted.Add(verdict);
                    break;
            }

            _log.LogInformation("correction {Rec} [{Start},{End}) '{Before}': {Outcome} ({Reason})",
                recordingId, candidate.Span.Start, candidate.Span.End, candidate.Before,
                verdict.Outcome, verdict.Reason);
        }

        return new CorrectionResult(baseText, diffs, openPoints, omitted);
    }
}

/// <summary>
/// The output of the correction stage: the UNCHANGED base text, the gated diff set, the escalated
/// open-points, and the omitted gaps. The base is never rewritten — applying <see cref="Diffs"/>
/// to <see cref="BaseText"/> is a separate, reviewable step; nothing here mutates the transcript.
/// </summary>
public sealed record CorrectionResult(
    string BaseText,
    IReadOnlyList<CorrectionDiff> Diffs,
    IReadOnlyList<CorrectionVerdict> OpenPoints,
    IReadOnlyList<CorrectionVerdict> Omitted);
