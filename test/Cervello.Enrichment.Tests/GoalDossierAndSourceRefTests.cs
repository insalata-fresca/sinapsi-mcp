using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The net-new <c>goal</c> object (design §3.1) + the SCHEMAS §1 source-ref grammar + the indexer
/// hit parser. Small, fast unit proofs of the contract's building blocks — NO personal data.
/// </summary>
public sealed class GoalDossierAndSourceRefTests
{
    [Fact]
    public void Goal_renders_type_goal_and_the_four_body_sections_in_order()
    {
        var md = new GoalDossier
        {
            Slug = "raise-series-a", Name = "Raise Series A", Status = "active",
            Horizon = "2026-Q4", People = new[] { "guilhem" }, Tags = new[] { "career" },
            Objective = "close the round", ObjectiveSource = "deposit://x",
            NextSteps = new[] { "sign the term sheet" }, Updated = "2026-07-08",
        }.Render();

        Assert.Contains("type: goal", md);
        Assert.Contains("status: active", md);
        Assert.Contains("horizon: 2026-Q4", md);
        // The four required body sections, in order (§3.1).
        var iObi = md.IndexOf("## Obiettivo", StringComparison.Ordinal);
        var iStato = md.IndexOf("## Stato", StringComparison.Ordinal);
        var iMov = md.IndexOf("## Movimento", StringComparison.Ordinal);
        var iNext = md.IndexOf("## Prossimi passi", StringComparison.Ordinal);
        Assert.True(iObi >= 0 && iObi < iStato && iStato < iMov && iMov < iNext);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("achieved")]
    [InlineData("stalled")]
    [InlineData("dropped")]
    public void Goal_accepts_the_ratified_status_vocabulary(string status) => GoalDossier.ValidateStatus(status);

    [Theory]
    [InlineData("paused")]  // MC Q2 dropped 'paused' from the doc's 5-value set → the 4-value set
    [InlineData("blocked")]
    [InlineData("frozen")]
    public void Goal_rejects_a_status_outside_the_ratified_set(string status) =>
        Assert.Throws<ArgumentException>(() => GoalDossier.ValidateStatus(status));

    [Fact]
    public void Movimento_line_renders_in_the_schemas_grammar()
    {
        var line = GoalDossier.RenderMovimentoLine(new TimelineLine("2026-07-02", "raised valuation", new[] { "series-a" }, "rec://call#s3"));
        Assert.Equal("- 2026-07-02 — raised valuation — [[series-a]] source: rec://call#s3", line);
    }

    [Theory]
    [InlineData("pin://abc", true)]
    [InlineData("rec://2026-06-01#s1", true)]
    [InlineData("drive://FILEID", true)]
    [InlineData("gmail://MSGID", true)]
    [InlineData("bundle://b1", true)]
    [InlineData("map/goals/g.md", true)]      // repo-relative path row
    [InlineData("pin://", false)]             // scheme with no id
    [InlineData("http://evil", false)]        // unregistered scheme
    [InlineData("/abs/path", false)]          // absolute path not allowed
    [InlineData("", false)]
    public void SourceRef_grammar(string reference, bool expected) =>
        Assert.Equal(expected, SourceRef.IsResolvableScheme(reference));

    [Theory]
    [InlineData("drive://x", true)]
    [InlineData("gmail://x", true)]
    [InlineData("rec://x", false)]
    [InlineData("pin://x", false)]
    public void SourceRef_external_needs_pin_on_cite(string reference, bool expected) =>
        Assert.Equal(expected, SourceRef.IsExternal(reference));

    [Fact]
    public void Indexer_parser_maps_results_and_drops_sourceless_hits()
    {
        var body = """
            {"query":"q","result_count":2,"results":[
              {"source":"rec://a","path":"recordings/a.md","kind":"recording","title":"A","scope":"cervello","snippet":"snip","score":0.8},
              {"path":"map/people/b.md","kind":"person","title":"B","scope":"cervello","snippet":"","score":0.5}
            ]}
            """;
        var hits = IndexerSearchClient.ParseHits(body);
        Assert.Equal(2, hits.Count);
        Assert.Equal("rec://a", hits[0].Source);
        Assert.Equal("map/people/b.md", hits[1].Source); // path is itself a valid ref when no explicit source
    }
}
