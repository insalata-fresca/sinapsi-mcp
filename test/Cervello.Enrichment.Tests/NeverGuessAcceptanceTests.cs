using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The SYSTEM-LEVEL never-guess acceptance test (task E5 4.5; spec <c>speaker-attribution</c> →
/// "Below-reject with no prior is omitted, never guessed" + "Basis-less attribution is rejected").
///
/// <para>This drives the REAL decision chain (attribute → apply → answer) over a MIXED input — a
/// clean resolvable speaker, an ambiguous one, and a totally ungrounded one — and asserts the
/// engine's headline invariant across the whole chain:</para>
///
/// <list type="number">
/// <item>EVERY fact that reaches <c>map/</c> carries a valid <c>source:</c> ref AND a parseable
///   confirmation basis (<c>auto://…@…</c> or <c>human://…</c>) — lint R1 + R9;</item>
/// <item>everything ungrounded is ESCALATED (open-point) or OMITTED — never asserted;</item>
/// <item>a below-reject, no-prior speaker becomes "unidentified" (omitted), never a guess;</item>
/// <item>nothing is INVENTED — every applied person/value traces to an input the engine was given.</item>
/// </list>
///
/// <para>The invariant is checked by the <see cref="AssertNeverGuessed"/> guard applied to every
/// PR the apply/answer path opens. Synthetic vectors only; no personal audio.</para>
/// </summary>
public sealed class NeverGuessAcceptanceTests
{
    private const string Rec = "rec-2026-07-04-standup";
    private const string Bundle = "2026-07-04-standup";
    private const string Token = "acceptance-token";
    private static readonly DateOnly Day = new(2026, 7, 4);

    private static MergedCluster Cluster(string speaker, float[] centroid) =>
        new(speaker, [speaker], centroid, [new DiarizedSegment(speaker, 0, 5)]);

    private static async Task<InMemoryVoiceprintStore> EnrolledStore(params (string slug, float[] vec)[] people)
    {
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist(people.Select(p => p.slug)));
        var e = new VoiceprintEnrollment(store);
        foreach (var (slug, vec) in people)
            await e.EnrollOnConfirmationAsync(slug, vec, [$"rec://seed#{slug}"], null, ConfirmationBasis.Human($"seed-{slug}"), Day);
        return store;
    }

    /// <summary>
    /// THE INVARIANT. For every mutation in an opened PR: a non-empty <c>source:</c>, a parseable
    /// basis, the basis echoed in the mutation, and no fabricated value (every person/value is in
    /// <paramref name="knownInputs"/>). Throws (fails the test) on any violation.
    /// </summary>
    private static void AssertNeverGuessed(MapReviewPr? pr, ISet<string> knownInputs)
    {
        if (pr is null) return; // no PR = nothing asserted = trivially safe
        foreach (var m in pr.Mutations)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Source), "every applied fact must carry a source: ref (R1)");
            Assert.False(string.IsNullOrWhiteSpace(m.BasisId), "every applied attribution must carry a basis (R9)");
            Assert.True(ConfirmationBasis.TryParse(m.BasisId, out _),
                $"basis '{m.BasisId}' must parse as auto://…@… or human://… (R9), never a bare/guessed claim");
            // No invention: the mutation must reference a known input person/value.
            Assert.True(knownInputs.Any(k => m.Content.Contains(k, StringComparison.Ordinal) || m.DossierPath.Contains(k, StringComparison.Ordinal)),
                $"mutation '{m.Content}' references no known input — possible invention");
        }
    }

    // ── the end-to-end never-guess assertion across a mixed recording ───────────────────────────
    [Fact]
    public async Task The_engine_never_emits_an_unsourced_or_guessed_attribution_across_the_chain()
    {
        // Enrolled people: guilhem (clean), marco + mara (near-identical → ambiguity source).
        var store = await EnrolledStore(
            ("guilhem", TestVectors.Axis(0)),
            ("marco", TestVectors.Axis(30)),
            ("mara", TestVectors.TiltedFromAxis(30, 31, 0.999))); // sits on top of marco → both ≥ auto
        // No participant hint → the ungrounded speaker (s3) becomes a local "Unknown speaker N".
        var attribution = new AttributionStage(store, new DecisionPolicy(DecisionBands.Default, PolicyPhase.GradedAutoApply));

        var verdicts = (await attribution.ResolveAsync(Rec, new[]
        {
            Cluster("s1", TestVectors.TiltedFromAxis(0, 5, 0.9)),   // clean → guilhem (enrolled, auto)
            Cluster("s2", TestVectors.TiltedFromAxis(30, 40, 0.95)),// ambiguous marco/mara → escalate
            Cluster("s3", TestVectors.Axis(99)),                    // orthogonal, no enrolled match, no hint
        })).Verdicts;

        // Sanity on the decision itself: s1 resolves cleanly; s2 + s3 are NEVER auto-applied
        // (s2 ambiguous enrolled; s3 no enrolled match + no hint → local "Unknown speaker N").
        Assert.Equal(AttributionOutcome.AutoApplied, verdicts[0].Outcome);
        Assert.Equal(AttributionOutcome.OpenPoint, verdicts[1].Outcome);
        Assert.NotEqual(AttributionOutcome.AutoApplied, verdicts[2].Outcome); // never guessed
        Assert.Null(verdicts[2].Person);   // no identity asserted for the ungrounded speaker
        Assert.Null(verdicts[2].Basis);    // no basis fabricated
        Assert.Equal("Unknown speaker 3", verdicts[2].LocalUnknownLabel); // recording-local label

        // Apply.
        var pr = new FakeMapPrWriter();
        var writer = new CervelloGraphWriter(pr, new FakeLinkResolver("guilhem", "marco", "mara"), new FakePinStore());
        var points = new InMemoryOpenPointStore();
        var apply = new ApplyStage(writer, points);
        var applied = await apply.ApplyAsync(new ApplyInput(Bundle, Rec, "2026-07-04",
            verdicts, Array.Empty<CorrectionVerdict>(), Array.Empty<MapMutation>(), Array.Empty<ReferencedLink>()));

        // INVARIANT: whatever reached map/ is sourced + based + not invented.
        var known = new HashSet<string>(StringComparer.Ordinal) { "guilhem", "marco", "mara", Rec };
        AssertNeverGuessed(pr.LastPr, known);

        // s2 (ambiguous enrolled) escalates to an open-point; s3 (no match, no hint) is a local unknown
        // (omitted) — neither reaches the map.
        Assert.Equal(1, applied.OpenPoints);
        Assert.Equal(1, applied.Omitted);
        // Exactly one applied attribution — guilhem — carries an auto:// basis.
        var m = Assert.Single(pr.LastPr!.Mutations);
        Assert.Contains("guilhem", m.DossierPath);
        Assert.StartsWith("auto://", m.BasisId);
    }

    // ── the escalated ambiguity is resolvable ONLY by a human answer, which is basis'd + sourced ─
    [Fact]
    public async Task An_escalated_point_is_only_resolved_by_a_human_answer_carrying_a_valid_basis()
    {
        var points = new InMemoryOpenPointStore();
        await points.EnqueueAsync(new OpenPoint("op_amb", OpenPointKind.Speaker, Rec, Bundle,
            "which enrolled person is s2?",
            new[] { new ScoredCandidate("marco", 0.95, "voice 0.95"), new ScoredCandidate("mara", 0.95, "voice 0.95") },
            mergedSpeaker: "s2"));

        var pr = new FakeMapPrWriter();
        var writer = new CervelloGraphWriter(pr, new FakeLinkResolver("marco", "mara"), new FakePinStore());
        var log = new FakeAccessLog();
        var allowlist = new EnrollmentAllowlist(new[] { "marco" });
        var voice = new InMemoryVoiceprintStore(allowlist);
        var enrollSource = new FakeEnrollmentSourceProvider();
        enrollSource.Seed(Rec, "s2", new EnrollmentSource(TestVectors.Axis(30), new[] { $"rec://{Rec}#s2" }, 0.95));
        var svc = new OpenPointsService(new TokenOpenPointsAuthGate(Token), points, log, writer,
            new InMemoryCorrectionMapStore(), new VoiceprintEnrollment(voice), allowlist, enrollSource);

        var result = await svc.AnswerAsync(Token, "op_amb", OpenPointAnswer.Select("marco"), Day);

        // The human resolution wrote a fact — and it is sourced + basis'd, never a guess.
        Assert.Equal(AnswerStatus.Applied, result.Status);
        AssertNeverGuessed(pr.LastPr, new HashSet<string>(StringComparer.Ordinal) { "marco", Rec });
        var m = Assert.Single(pr.LastPr!.Mutations);
        Assert.Equal("human://op_amb", m.BasisId);
        Assert.True(ConfirmationBasis.TryParse(m.BasisId, out _));
    }

    // ── a basis-less attribution can never even be constructed (R9 floor) ────────────────────────
    [Fact]
    public void A_basis_less_applied_attribution_cannot_be_constructed_R9()
    {
        // AutoApplied REQUIRES a basis; there is no factory path to an applied verdict without one.
        Assert.Throws<ArgumentNullException>(() =>
            AttributionVerdict.AutoApplied("s1", "guilhem", 0.9, $"rec://{Rec}#s1", basis: null!));
        // A bare "X said" string is not a valid basis.
        Assert.False(ConfirmationBasis.TryParse("guilhem said so", out _));
        Assert.False(ConfirmationBasis.TryParse("", out _));
    }

    // ── below-reject, no prior → "unidentified" (omitted), never a guess ────────────────────────
    [Fact]
    public async Task Below_reject_no_prior_is_left_unidentified_never_guessed()
    {
        var store = await EnrolledStore(("guilhem", TestVectors.Axis(0)));
        // No participant hint → the ungrounded voice becomes a local "Unknown speaker N" (omitted).
        var attribution = new AttributionStage(store, new DecisionPolicy(DecisionBands.Default, PolicyPhase.GradedAutoApply));

        var verdicts = (await attribution.ResolveAsync(Rec, new[] { Cluster("s1", TestVectors.Axis(88)) })).Verdicts;

        var v = Assert.Single(verdicts);
        Assert.Equal(AttributionOutcome.Omitted, v.Outcome);
        Assert.Null(v.Person);      // no identity asserted
        Assert.Null(v.Basis);       // no basis fabricated
        Assert.Equal("Unknown speaker 1", v.LocalUnknownLabel);

        // And it never reaches map/.
        var pr = new FakeMapPrWriter();
        var apply = new ApplyStage(new CervelloGraphWriter(pr, new FakeLinkResolver(), new FakePinStore()), new InMemoryOpenPointStore());
        var applied = await apply.ApplyAsync(new ApplyInput(Bundle, Rec, "2026-07-04",
            verdicts, Array.Empty<CorrectionVerdict>(), Array.Empty<MapMutation>(), Array.Empty<ReferencedLink>()));
        Assert.Null(applied.Pr);
        Assert.Equal(1, applied.Omitted);
    }
}
