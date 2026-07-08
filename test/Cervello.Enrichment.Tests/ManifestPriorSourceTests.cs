using Cervello.Enrichment.Adapters;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Live <see cref="ManifestPriorSource"/> against a TEMP working tree holding <c>map/people/*.md</c>
/// dossiers + a hand-rolled <c>recordings/manifest.yaml</c> (§8, the shape <c>YamlManifestStore</c>
/// writes, extended with an optional <c>participants:</c> list). Proves the prior is DETERMINISTIC,
/// GROUNDS every candidate against a real dossier (never invents a person), reports STRONG only for a
/// manifest-participants prior, and never resolves an identity by itself.
/// </summary>
public sealed class ManifestPriorSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cervello-prior-" + Guid.NewGuid().ToString("N"));

    public ManifestPriorSourceTests() => Directory.CreateDirectory(Path.Combine(_root, "map", "people"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private void Dossier(string slug) =>
        File.WriteAllText(Path.Combine(_root, "map", "people", $"{slug}.md"), $"---\ntype: person\nname: {slug}\n---\n");

    private void Manifest(string yaml)
    {
        Directory.CreateDirectory(Path.Combine(_root, "recordings"));
        File.WriteAllText(Path.Combine(_root, "recordings", "manifest.yaml"), yaml);
    }

    [Fact]
    public async Task Filename_slug_token_matching_a_dossier_becomes_a_weak_candidate()
    {
        Dossier("guilhem");
        var src = new ManifestPriorSource(_root);

        // id = {yyyyMMdd}-{slug(basename)} — "Guilhem 121" → 20260704-guilhem-121
        var prior = await src.GetPriorAsync("20260704-guilhem-121");

        Assert.Contains("guilhem", prior.CandidatePersonSlugs);
        Assert.False(prior.IsStrong); // filename-only ⇒ weak (narrows, never conflict-escalates)
    }

    [Fact]
    public async Task Filename_token_matching_NO_dossier_is_dropped_never_invented()
    {
        // No dossier for "guilhem" — the never-guess floor: a mis-heard basename fabricates nothing.
        var src = new ManifestPriorSource(_root);

        var prior = await src.GetPriorAsync("20260704-guilhem-121");

        Assert.Empty(prior.CandidatePersonSlugs);
        Assert.False(prior.IsStrong);
    }

    [Fact]
    public async Task Manifest_participants_are_a_strong_grounded_prior()
    {
        Dossier("marco");
        Dossier("mara");
        Manifest(
            "- id: 20260704-standup\n" +
            "  audio_sha256: abc\n" +
            "  participants: [marco, mara]\n" +
            "  state: normalized\n");
        var src = new ManifestPriorSource(_root);

        var prior = await src.GetPriorAsync("20260704-standup");

        Assert.Contains("marco", prior.CandidatePersonSlugs);
        Assert.Contains("mara", prior.CandidatePersonSlugs);
        Assert.True(prior.IsStrong); // an explicit operator-recorded roster ⇒ strong
    }

    [Fact]
    public async Task Manifest_block_list_participants_parse_too()
    {
        Dossier("marco");
        Manifest(
            "- id: 20260704-standup\n" +
            "  audio_sha256: abc\n" +
            "  participants:\n" +
            "    - marco\n" +
            "    - unknown-person\n" + // no dossier → dropped
            "  state: normalized\n");
        var src = new ManifestPriorSource(_root);

        var prior = await src.GetPriorAsync("20260704-standup");

        Assert.Equal(["marco"], prior.CandidatePersonSlugs);
        Assert.True(prior.IsStrong);
    }

    [Fact]
    public async Task Manifest_and_filename_candidates_union_manifest_first()
    {
        Dossier("marco");
        Dossier("guilhem");
        Manifest(
            "- id: 20260704-guilhem-standup\n" +
            "  participants: [marco]\n");
        var src = new ManifestPriorSource(_root);

        var prior = await src.GetPriorAsync("20260704-guilhem-standup");

        // manifest (strong) first, then the grounded filename token.
        Assert.Equal(["marco", "guilhem"], prior.CandidatePersonSlugs);
        Assert.True(prior.IsStrong);
        Assert.True(prior.Includes("guilhem"));
    }

    [Fact]
    public async Task No_manifest_file_degrades_to_filename_only()
    {
        Dossier("guilhem");
        var src = new ManifestPriorSource(_root); // no manifest written

        var prior = await src.GetPriorAsync("20260704-guilhem");

        Assert.Equal(["guilhem"], prior.CandidatePersonSlugs);
        Assert.False(prior.IsStrong);
    }

    [Fact]
    public async Task Deterministic_same_input_same_output()
    {
        Dossier("marco");
        Manifest("- id: 20260704-x\n  participants: [marco]\n");
        var src = new ManifestPriorSource(_root);

        var a = await src.GetPriorAsync("20260704-x");
        var b = await src.GetPriorAsync("20260704-x");

        Assert.Equal(a.CandidatePersonSlugs, b.CandidatePersonSlugs);
        Assert.Equal(a.IsStrong, b.IsStrong);
    }
}
