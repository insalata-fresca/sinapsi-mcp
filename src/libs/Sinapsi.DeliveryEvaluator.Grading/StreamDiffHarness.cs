namespace Sinapsi.DeliveryEvaluator.Grading;

/// <summary>
/// The SHADOW-vs-WOULD-ENFORCE stream-diff harness that AUTO-FAILS promotion on deviation
/// (home-server <c>docs/64 §4</c> Track B: "shadow-vs-would-enforce stream-diff scorecard that
/// auto-fails promotion").
///
/// <para><b>What it proves.</b> Before any shadow→enforce flip, every decision observed on the
/// shadow stream is replayed through the CURRENT (candidate) evaluator to compute what enforcement
/// WOULD do on the same change. For a deterministic evaluator the two must be identical: a single
/// deviation means the candidate would enforce differently than what shadow observed — drift or
/// non-determinism — and the promotion gate FAILS. Deviations are further split by SAFETY
/// direction: an <see cref="DeviationDirection.MorePermissive"/> deviation (would-enforce ALLOWS
/// what shadow escalated/denied) is the critical regression that lets a risky change through.</para>
///
/// <para><b>Fail-safe on unverifiable records.</b> A shadow record that does not carry the change
/// (<see cref="ShadowDecision.CanRecompute"/> = false) cannot be diffed; it is counted as an
/// unverifiable gap and BLOCKS promotion (never silently passed). Wiring the live shadow stream to
/// the change by <c>correlation_id</c> so every record is recomputable is the flagged LIVE-WIRE
/// follow-on.</para>
/// </summary>
public static class StreamDiffHarness
{
    /// <summary>Diff a shadow stream against would-enforce and produce the auto-fail report.</summary>
    public static StreamDiffReport Diff(IEnumerable<ShadowDecision> stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        var rows = new List<StreamDiffRow>();
        foreach (var d in stream)
        {
            if (!d.CanRecompute)
            {
                rows.Add(new StreamDiffRow(d.CorrelationId, d.ShadowVerdict, WouldEnforce: null,
                    Status: DiffStatus.Unverifiable, Direction: DeviationDirection.None));
                continue;
            }

            var change = CorpusScenarioAdapter.ToChangeSet(d.DiffSummary!, correlationId: d.CorrelationId);
            var wouldEnforce = DeterministicRiskClassifier.Classify(change).Verdict;

            if (wouldEnforce == d.ShadowVerdict)
            {
                rows.Add(new StreamDiffRow(d.CorrelationId, d.ShadowVerdict, wouldEnforce,
                    DiffStatus.Match, DeviationDirection.None));
            }
            else
            {
                var dir = Permissiveness(wouldEnforce) > Permissiveness(d.ShadowVerdict)
                    ? DeviationDirection.MorePermissive   // would-enforce allows more than shadow did — UNSAFE
                    : DeviationDirection.Stricter;         // would-enforce blocks more than shadow did — safe-but-noisy
                rows.Add(new StreamDiffRow(d.CorrelationId, d.ShadowVerdict, wouldEnforce,
                    DiffStatus.Deviation, dir));
            }
        }

        return new StreamDiffReport(rows);
    }

    // allow is the most permissive; deny the least. Higher = more permissive.
    private static int Permissiveness(Verdict v) => v switch
    {
        Verdict.Allow => 2,
        Verdict.RequiresApproval => 1,
        Verdict.Deny => 0,
        _ => 1,
    };
}

/// <summary>The status of one diffed shadow decision.</summary>
public enum DiffStatus
{
    /// <summary>Would-enforce reproduced the shadow verdict exactly.</summary>
    Match,

    /// <summary>Would-enforce differs from the shadow verdict — a promotion-failing deviation.</summary>
    Deviation,

    /// <summary>The record could not be recomputed (no change carried) — a promotion-blocking gap.</summary>
    Unverifiable,
}

/// <summary>The safety direction of a deviation.</summary>
public enum DeviationDirection
{
    /// <summary>Not a deviation.</summary>
    None,

    /// <summary>Would-enforce is MORE permissive than shadow (allows what shadow held) — the
    /// critical, unsafe regression.</summary>
    MorePermissive,

    /// <summary>Would-enforce is STRICTER than shadow (holds what shadow allowed) — safe direction,
    /// but still a deviation that fails a strict reproduction gate.</summary>
    Stricter,
}

/// <summary>One row of the stream diff.</summary>
public sealed record StreamDiffRow(
    string CorrelationId,
    Verdict ShadowVerdict,
    Verdict? WouldEnforce,
    DiffStatus Status,
    DeviationDirection Direction);
