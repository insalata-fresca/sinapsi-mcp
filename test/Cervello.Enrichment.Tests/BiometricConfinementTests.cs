using System.Text.Json;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Biometric-confinement invariants as offline binary properties (spec <c>voiceprint-store</c> →
/// "Voiceprints never git"; DESIGN §10.4: embeddings never enter git, never leave CT146). We
/// assert that the OUTPUTS that DO cross the git / map boundary — the attribution verdict and the
/// dossier <c>voice:</c> line — never carry the 256-d vector, while the vector-bearing
/// <see cref="Voiceprint"/> is the store-only (CT146) type.
/// </summary>
public sealed class BiometricConfinementTests
{
    private static readonly DateOnly Day = new(2026, 7, 1);

    [Fact] // the dossier voice: line (SCHEMAS §2 — the ONLY thing that reaches the map) has no vector
    public async Task Dossier_voice_line_records_result_only_no_vector()
    {
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist(["guilhem"]));
        var res = await new VoiceprintEnrollment(store).EnrollOnConfirmationAsync(
            "guilhem", TestVectors.Axis(0), ["rec://r1#s1"], 0.83, ConfirmationBasis.Human("op_1"), Day);

        // The dossier line is a short human string, not a serialization of 256 floats.
        Assert.Equal("enrolled 2026-07-01, 1 samples, last-match 0.83", res.DossierVoiceLine);
        Assert.True(res.DossierVoiceLine.Length < 80);
    }

    [Fact] // an applied verdict serialized (as it would be into a bundle/map) carries NO embedding
    public void Serialized_attribution_verdict_contains_no_embedding_vector()
    {
        var v = AttributionVerdict.AutoApplied("s1", "guilhem", 0.83, "rec://r1#s1", ConfirmationBasis.Auto("v1"));
        var json = JsonSerializer.Serialize(v);

        // The public verdict shape has no float[] / vector member — only slug, confidence, source, basis.
        Assert.DoesNotContain("Centroid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vector", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rec://r1#s1", json);
        Assert.Contains("auto://voice-match@v1", json);
    }

    [Fact] // the vector-bearing type is confined: it exposes the centroid but is the store-only shape
    public void Voiceprint_is_the_only_vector_bearing_type_and_is_store_scoped()
    {
        // A voiceprint on a distinctive fractional axis so the vector value is unmistakable.
        var vp = new Voiceprint("guilhem", TestVectors.TiltedFromAxis(0, 1, 0.62), 3, Day, 0.77, ["rec://r1#s1"]);
        Assert.Equal(SpeakerEmbedding.ExpectedDim, vp.Centroid.Count);

        // It lives under Adapters/Domain feeding IVoiceprintStore (CT146) — never a bundle/map DTO.
        // The dossier projection (SCHEMAS §2) records the RESULT only — the raw centroid float
        // (0.62 on axis 0) never appears in the map-bound line.
        var line = vp.DossierVoiceLine();
        Assert.Equal("enrolled 2026-07-01, 3 samples, last-match 0.77", line);
        Assert.DoesNotContain("0.62", line);
    }

    [Fact] // the E3 additions keep the no-NATS confinement (extends the E2b assembly invariant)
    public void Enrichment_assembly_still_references_no_nats_after_e3()
    {
        var asm = typeof(AttributionStage).Assembly;
        foreach (var name in asm.GetReferencedAssemblies().Select(a => a.Name ?? ""))
        {
            Assert.False(name.Contains("Sinapsi.Nats", StringComparison.OrdinalIgnoreCase), $"unexpected {name}");
            Assert.False(name.StartsWith("NATS.", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("NATS", StringComparison.OrdinalIgnoreCase), $"unexpected {name}");
        }
    }
}
