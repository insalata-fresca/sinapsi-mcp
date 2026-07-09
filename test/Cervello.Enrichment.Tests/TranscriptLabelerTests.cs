using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// M5 — metadata-informed correction + speaker labels (design <c>ste/cervello</c>
/// <c>docs/design/autonomous-attribution.md</c> §5 M5). Proves <see cref="TranscriptLabeler"/> is a
/// pure, mechanical APPLICATION of an already-gated diff set (never re-decides a diff) and that the
/// speaker roster it builds from an <see cref="AttributionResult"/> NEVER fabricates a name: only an
/// applied verdict (real name) or an explicit local-unknown label reaches the roster; an unconfirmed
/// open-point or a plain omit contributes nothing.
/// </summary>
public sealed class TranscriptLabelerTests
{
    // ── ApplyDiffs: a single diff replaces the exact span, nothing else changes ─────────────────
    [Fact]
    public void ApplyDiffs_replaces_the_exact_span_leaving_the_rest_untouched()
    {
        const string baseText = "we reviewed the Total Energies contract today";
        var span = SpanOf(baseText, "Total Energies");
        var diff = new CorrectionDiff(span, "Total Energies", "TotalEnergies", CorrectionKind.Term, 0.9,
            "glossary://Total Energies");

        var corrected = TranscriptLabeler.ApplyDiffs(baseText, [diff]);

        Assert.Equal("we reviewed the TotalEnergies contract today", corrected);
    }

    // ── ApplyDiffs: multiple non-overlapping diffs all apply correctly regardless of order ──────
    [Fact]
    public void ApplyDiffs_applies_multiple_diffs_correctly_regardless_of_span_order()
    {
        const string baseText = "Gilan met Ale about the Total Energies deal";
        var nameSpan = SpanOf(baseText, "Gilan");
        var aleSpan = SpanOf(baseText, "Ale");
        var termSpan = SpanOf(baseText, "Total Energies");
        var diffs = new[]
        {
            new CorrectionDiff(termSpan, "Total Energies", "TotalEnergies", CorrectionKind.Term, 0.9, "glossary://x"),
            new CorrectionDiff(nameSpan, "Gilan", "Guilhem", CorrectionKind.Name, 0.9, "[[guilhem]]"),
            new CorrectionDiff(aleSpan, "Ale", "Alessandro", CorrectionKind.Name, 0.9, "[[alessandro]]"),
        };

        var corrected = TranscriptLabeler.ApplyDiffs(baseText, diffs);

        Assert.Equal("Guilhem met Alessandro about the TotalEnergies deal", corrected);
    }

    // ── ApplyDiffs: an empty diff set returns the base text unchanged (never a rewrite) ─────────
    [Fact]
    public void ApplyDiffs_with_no_diffs_returns_the_base_text_unchanged()
    {
        const string baseText = "nothing to correct here";
        Assert.Equal(baseText, TranscriptLabeler.ApplyDiffs(baseText, Array.Empty<CorrectionDiff>()));
    }

    // ── BuildRoster: an applied (named) verdict produces a real-name roster line ─────────────────
    [Fact]
    public void BuildRoster_names_a_speaker_only_from_an_applied_verdict()
    {
        var basis = ConfirmationBasis.Auto("v1");
        var verdict = AttributionVerdict.AutoApplied("s1", "guilhem", 0.9, "rec://rec-1#s1", basis);
        var result = new AttributionResult([verdict], Array.Empty<EnrollmentProposal>());

        var roster = TranscriptLabeler.BuildRoster(result);

        var entry = Assert.Single(roster);
        Assert.Equal("s1", entry.MergedSpeaker);
        Assert.Equal("guilhem", entry.DisplayLabel);
        Assert.True(entry.Named);
    }

    // ── BuildRoster: a LocalUnknownLabel verdict produces "Unknown speaker N", never a real name ──
    [Fact]
    public void BuildRoster_uses_the_local_unknown_label_never_a_fabricated_name()
    {
        var verdict = AttributionVerdict.UnknownLocal("s3", "Unknown speaker 1");
        var result = new AttributionResult([verdict], Array.Empty<EnrollmentProposal>());

        var roster = TranscriptLabeler.BuildRoster(result);

        var entry = Assert.Single(roster);
        Assert.Equal("s3", entry.MergedSpeaker);
        Assert.Equal("Unknown speaker 1", entry.DisplayLabel);
        Assert.False(entry.Named);
    }

    // ── HEADLINE NEGATIVE: an OpenPoint (unconfirmed) verdict NEVER gets a roster line — no name ──
    // ── is ever surfaced for a speaker the operator has not confirmed. ──────────────────────────
    [Fact]
    public void BuildRoster_never_labels_an_unconfirmed_open_point()
    {
        var openPoint = AttributionVerdict.OpenPoint("s1", 0.9, "who is speaker s1?");
        var result = new AttributionResult([openPoint], Array.Empty<EnrollmentProposal>());

        var roster = TranscriptLabeler.BuildRoster(result);

        Assert.Empty(roster);
    }

    // ── HEADLINE NEGATIVE: a plain Omitted verdict (no local-unknown label) NEVER gets a roster ───
    // ── line — an unidentified, non-local-labeled speaker is left off the roster entirely. ────────
    [Fact]
    public void BuildRoster_never_labels_a_plain_omitted_verdict_with_no_local_label()
    {
        var omitted = AttributionVerdict.Omitted("s2", "below reject band, no confirming prior");
        var result = new AttributionResult([omitted], Array.Empty<EnrollmentProposal>());

        var roster = TranscriptLabeler.BuildRoster(result);

        Assert.Empty(roster);
    }

    // ── BuildRoster: a mixed recording — named, unknown, and open-point voices — only surfaces ────
    // ── the named + local-unknown ones; the open-point contributes nothing. ─────────────────────
    [Fact]
    public void BuildRoster_mixed_recording_only_surfaces_named_and_local_unknown_entries()
    {
        var basis = ConfirmationBasis.Auto("v1");
        var verdicts = new[]
        {
            AttributionVerdict.AutoApplied("s1", "guilhem", 0.9, "rec://rec-1#s1", basis),
            AttributionVerdict.UnknownLocal("s2", "Unknown speaker 1"),
            AttributionVerdict.OpenPoint("s3", 0.5, "ambiguous"),
        };
        var result = new AttributionResult(verdicts, Array.Empty<EnrollmentProposal>());

        var roster = TranscriptLabeler.BuildRoster(result);

        Assert.Equal(2, roster.Count);
        Assert.Contains(roster, r => r is { MergedSpeaker: "s1", DisplayLabel: "guilhem", Named: true });
        Assert.Contains(roster, r => r is { MergedSpeaker: "s2", DisplayLabel: "Unknown speaker 1", Named: false });
        Assert.DoesNotContain(roster, r => r.MergedSpeaker == "s3");
    }

    // ── RenderRosterSection: an empty roster renders nothing (no audio / nothing resolvable) ──────
    [Fact]
    public void RenderRosterSection_empty_roster_renders_empty_string()
    {
        Assert.Equal("", TranscriptLabeler.RenderRosterSection(Array.Empty<SpeakerLabel>()));
    }

    // ── RenderRosterSection: renders a markdown section with one line per roster entry ────────────
    [Fact]
    public void RenderRosterSection_renders_one_line_per_entry()
    {
        var roster = new[]
        {
            new SpeakerLabel("s1", "guilhem", true),
            new SpeakerLabel("s2", "Unknown speaker 1", false),
        };

        var section = TranscriptLabeler.RenderRosterSection(roster);

        Assert.Contains("## Speakers", section);
        Assert.Contains("s1 — guilhem", section);
        Assert.Contains("s2 — Unknown speaker 1", section);
    }

    private static TextSpan SpanOf(string text, string substring)
    {
        var i = text.IndexOf(substring, StringComparison.Ordinal);
        if (i < 0) throw new ArgumentException($"'{substring}' not in base");
        return new TextSpan(i, i + substring.Length);
    }
}
