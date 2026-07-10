using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Math;
using Cervello.Enrichment.Pipeline;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The voiceprint store enroll/refine + deletion-runbook + allowlist invariants (spec
/// <c>voiceprint-store</c>; DESIGN §10; OPERATIONS §7). SYNTHETIC 256-d vectors only — no
/// personal audio, no real biometric vector ever appears here.
/// </summary>
public sealed class VoiceprintStoreTests
{
    private static readonly DateOnly Day1 = new(2026, 7, 1);
    private static readonly DateOnly Day2 = new(2026, 7, 8);

    private static EnrollmentAllowlist Allow(params string[] slugs) => new(slugs);

    private static VoiceprintEnrollment Enrollment(InMemoryVoiceprintStore store) => new(store);

    // ---- enroll / refine on confirmation, under the allowlist ----------------------------

    [Fact] // scenario: Confirmation enrolls — dossier voice line has no vector
    public async Task Confirmation_enrolls_and_dossier_line_records_result_not_vector()
    {
        var store = new InMemoryVoiceprintStore(Allow("guilhem"));
        var res = await Enrollment(store).EnrollOnConfirmationAsync(
            "guilhem", TestVectors.Axis(1), ["rec://r1#s1"], matchCosine: null,
            ConfirmationBasis.Human("op_1"), Day1);

        Assert.False(res.WasRefine);
        Assert.Equal(1, res.Print.SampleCount);
        Assert.Equal("enrolled 2026-07-01, 1 samples", res.DossierVoiceLine);
        Assert.DoesNotContain("256", res.DossierVoiceLine); // no vector / dimension leaked
        Assert.DoesNotContain("[", res.DossierVoiceLine);   // no array of floats
    }

    [Fact] // scenario: Subsequent confirmation refines — sample_count increments to 2
    public async Task Subsequent_confirmation_refines_and_increments_sample_count()
    {
        var store = new InMemoryVoiceprintStore(Allow("guilhem"));
        var e = Enrollment(store);
        await e.EnrollOnConfirmationAsync("guilhem", TestVectors.Axis(1), ["rec://r1#s1"], 0.8,
            ConfirmationBasis.Human("op_1"), Day1);
        var res = await e.EnrollOnConfirmationAsync("guilhem", TestVectors.Axis(1), ["rec://r2#s1"], 0.9,
            ConfirmationBasis.Human("op_2"), Day2);

        Assert.True(res.WasRefine);
        Assert.Equal(2, res.Print.SampleCount);
        Assert.Equal("enrolled 2026-07-01, 2 samples, last-match 0.9", res.DossierVoiceLine);
    }

    [Fact] // scenario: No enrollment without confirmation — allowlist hard gate refuses (never silent)
    public async Task Enrollment_refused_for_person_not_on_allowlist()
    {
        var store = new InMemoryVoiceprintStore(Allow("guilhem")); // petter NOT allowed
        await Assert.ThrowsAsync<EnrollmentNotAllowedException>(() =>
            Enrollment(store).EnrollOnConfirmationAsync(
                "petter", TestVectors.Axis(2), ["rec://r1#s1"], 0.8,
                ConfirmationBasis.Human("op_1"), Day1));

        Assert.Null(await store.GetAsync("petter")); // nothing was written
    }

    [Fact] // refine keeps the running centroid faithful (weighted mean over samples)
    public async Task Refine_produces_weighted_running_centroid()
    {
        var store = new InMemoryVoiceprintStore(Allow("x"));
        var e = Enrollment(store);
        // enroll on axis 0, refine toward a tilted vector; centroid should sit between them.
        await e.EnrollOnConfirmationAsync("x", TestVectors.Axis(0), ["rec://r1#s1"], null,
            ConfirmationBasis.Human("a"), Day1);
        var res = await e.EnrollOnConfirmationAsync(
            "x", TestVectors.TiltedFromAxis(0, 1, 0.5), ["rec://r2#s1"], null,
            ConfirmationBasis.Human("b"), Day2);

        var cosToAxis0 = Cosine.Similarity(res.Print.Centroid, TestVectors.Axis(0));
        Assert.InRange(cosToAxis0, 0.5, 1.0); // pulled off pure axis0 but still closest to it
    }

    // ---- match ---------------------------------------------------------------------------

    [Fact]
    public async Task Match_returns_enrolled_people_ordered_by_cosine()
    {
        var store = new InMemoryVoiceprintStore(Allow("a", "b"));
        var e = Enrollment(store);
        await e.EnrollOnConfirmationAsync("a", TestVectors.Axis(0), ["rec://r#s1"], null, ConfirmationBasis.Human("1"), Day1);
        await e.EnrollOnConfirmationAsync("b", TestVectors.Axis(1), ["rec://r#s2"], null, ConfirmationBasis.Human("2"), Day1);

        var matches = await store.MatchAsync(TestVectors.TiltedFromAxis(0, 1, 0.9)); // closer to a
        Assert.Equal("a", matches[0].PersonSlug);
        Assert.True(matches[0].Cosine > matches[1].Cosine);
    }

    [Fact]
    public async Task Match_on_empty_store_returns_no_matches()
    {
        var store = new InMemoryVoiceprintStore(Allow("a"));
        Assert.Empty(await store.MatchAsync(TestVectors.Axis(0)));
    }

    // ---- deletion runbook (OPERATIONS §7) ------------------------------------------------

    [Fact] // scenario: Deletion removes the biometric basis, keeps confirmed text
    public async Task Deletion_removes_centroid_rows_and_audio_and_sets_voice_deleted()
    {
        var store = new InMemoryVoiceprintStore(Allow("x"));
        var e = Enrollment(store);
        await e.EnrollOnConfirmationAsync("x", TestVectors.Axis(0), ["rec://r1#s1"], 0.8,
            ConfirmationBasis.Human("op_1"), Day1);
        Assert.True(store.HasEnrollmentAudio("x"));

        var del = await e.DeleteVoiceprintAsync("x", Day2);

        Assert.True(del.PrintExisted);
        Assert.Equal("deleted 2026-07-08", del.DossierVoiceLine);
        Assert.True(del.RemarkPriorAttributionsHistorical);
        Assert.Null(await store.GetAsync("x"));            // centroid + rows gone
        Assert.False(store.HasEnrollmentAudio("x"));       // enrollment audio purged
        Assert.True(await store.IsDeletedAsync("x"));      // tombstoned (survives restore, lint R8)
        Assert.Empty(await store.MatchAsync(TestVectors.Axis(0))); // no longer matchable
    }

    [Fact] // a re-enrollment after deletion (deliberate re-consent) clears the tombstone
    public async Task Re_enrollment_after_deletion_clears_tombstone()
    {
        var store = new InMemoryVoiceprintStore(Allow("x"));
        var e = Enrollment(store);
        await e.EnrollOnConfirmationAsync("x", TestVectors.Axis(0), ["rec://r1#s1"], null, ConfirmationBasis.Human("1"), Day1);
        await e.DeleteVoiceprintAsync("x", Day2);
        Assert.True(await store.IsDeletedAsync("x"));

        await e.EnrollOnConfirmationAsync("x", TestVectors.Axis(0), ["rec://r2#s1"], null, ConfirmationBasis.Human("2"), Day2);
        Assert.False(await store.IsDeletedAsync("x")); // re-consented → no longer tombstoned
    }
}
