namespace Cervello.Enrichment.Domain;

/// <summary>
/// M5 — metadata-informed correction + speaker labels (design <c>ste/cervello</c>
/// <c>docs/design/autonomous-attribution.md</c> §5 M5). A small, pure, additive helper — NOT a new
/// correction stage — that (a) applies the already-evidence-gated <see cref="CorrectionDiff"/> set to
/// the base transcript to produce the corrected text (a diff APPLICATION, never a re-derivation of the
/// diffs themselves — <see cref="Pipeline.Stages.CorrectionStage"/> and its grader own that), and (b)
/// builds the recording's speaker roster strictly from the M4 <see cref="AttributionVerdict"/>s.
///
/// <para><b>Never-invent floor.</b> A roster entry names a speaker ONLY for an applied verdict
/// (<see cref="AttributionOutcome.AutoApplied"/>/<see cref="AttributionOutcome.Flagged"/>, carrying
/// <see cref="AttributionVerdict.Person"/>) or an explicit <see cref="AttributionVerdict.LocalUnknownLabel"/>
/// (the deterministic "Unknown speaker N" — still not a name, never fabricated). A plain
/// <see cref="AttributionOutcome.Omitted"/> with no local-unknown label, or an
/// <see cref="AttributionOutcome.OpenPoint"/> (unconfirmed — the operator has not answered), contributes
/// NO roster line: the speaker stays unlabeled rather than guessed.</para>
/// </summary>
public static class TranscriptLabeler
{
    /// <summary>
    /// Apply a set of evidence-gated <see cref="CorrectionDiff"/>s to <paramref name="baseText"/>,
    /// producing the corrected text. Diffs are applied right-to-left over their <see cref="TextSpan"/>s
    /// so earlier offsets are unaffected by later (higher-offset) replacements. This is a mechanical
    /// APPLICATION of an already-graded diff set — it invents nothing; a diff that reaches here has
    /// already cleared <see cref="Policy.CorrectionGrader"/>'s evidence gate.
    /// </summary>
    public static string ApplyDiffs(string baseText, IReadOnlyList<CorrectionDiff> diffs)
    {
        ArgumentNullException.ThrowIfNull(baseText);
        ArgumentNullException.ThrowIfNull(diffs);
        if (diffs.Count == 0)
            return baseText;

        // Sort by span start, descending, so each replacement's offset is stable regardless of the
        // ones already applied (replacing from the end of the string backward).
        var ordered = diffs.OrderByDescending(d => d.Span.Start).ToList();

        var sb = new System.Text.StringBuilder(baseText);
        foreach (var diff in ordered)
        {
            if (diff.Span.End > sb.Length || diff.Span.Start < 0)
                continue; // a span outside the current text bounds is skipped, never guessed/clamped
            sb.Remove(diff.Span.Start, diff.Span.Length);
            sb.Insert(diff.Span.Start, diff.After);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build the recording's speaker roster from the M4 <see cref="AttributionResult"/> — one entry per
    /// merged voice cluster that the attribution stage could say something about (named or locally
    /// unknown). A verdict with no name and no local-unknown label (a plain omit, or an unconfirmed
    /// open-point) contributes nothing — never a guessed or placeholder name.
    /// </summary>
    public static IReadOnlyList<SpeakerLabel> BuildRoster(AttributionResult attribution)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        var roster = new List<SpeakerLabel>(attribution.Verdicts.Count);
        foreach (var v in attribution.Verdicts)
        {
            if (v.IsApplied && v.Person is not null)
                roster.Add(new SpeakerLabel(v.MergedSpeaker, v.Person, Named: true));
            else if (v.LocalUnknownLabel is not null)
                roster.Add(new SpeakerLabel(v.MergedSpeaker, v.LocalUnknownLabel, Named: false));
            // else: OpenPoint (unconfirmed) or a plain Omitted with no local label → no roster line.
        }
        return roster;
    }

    /// <summary>
    /// Render the roster as a markdown "Speakers" section, or an empty string when the roster is empty
    /// (no audio / nothing resolvable — nothing to append).
    /// </summary>
    public static string RenderRosterSection(IReadOnlyList<SpeakerLabel> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (roster.Count == 0)
            return "";
        var sb = new System.Text.StringBuilder();
        sb.Append("\n## Speakers\n\n");
        foreach (var s in roster)
            sb.Append("- ").Append(s.MergedSpeaker).Append(" — ").Append(s.DisplayLabel).Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// One resolved speaker-roster line: the recording-local merged-cluster label (e.g. <c>s1</c>) and its
/// display label — either a real resolved name (<see cref="Named"/> true, sourced from an
/// <see cref="AttributionOutcome.AutoApplied"/>/<see cref="AttributionOutcome.Flagged"/> verdict) or the
/// deterministic "Unknown speaker N" placeholder (<see cref="Named"/> false, from
/// <see cref="AttributionVerdict.LocalUnknownLabel"/>) — never a fabricated name.
/// </summary>
public sealed record SpeakerLabel(string MergedSpeaker, string DisplayLabel, bool Named);
