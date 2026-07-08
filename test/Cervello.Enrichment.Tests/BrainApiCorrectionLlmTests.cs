using System.Net;
using System.Text.Json;
using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// L1 unit tests for the LIVE <see cref="BrainApiCorrectionLlm"/> (E4's deferred adapter) over a MOCK
/// HttpClient. Asserts the request carries the base text + glossary + participants (the grounding
/// context), the candidate mapping, and that a malformed/out-of-range candidate is DROPPED rather
/// than coerced into a phantom correction. The no-fabrication floor itself lives in the
/// CorrectionStage/Grader (which evidence-gate these UNGATED proposals) — verified in E4's suite;
/// here we prove the adapter transports the proposal faithfully. L2: the real brain-api LLM route.
/// </summary>
public sealed class BrainApiCorrectionLlmTests
{
    private static BrainApiCorrectionLlm Make(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://brain-api.test") }, new StaticBearerProvider("t"));

    private static CorrectionContext Context() => new(
        glossary: [new GlossaryEntry("Total Energies", "TotalEnergies", CorrectionKind.Term)],
        participants: [new ResolvedParticipant("guilhem", "Guilhem", ["Guilhome"])]);

    [Fact]
    public async Task Sends_base_text_glossary_and_participants_as_the_grounding_context()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{ "candidates": [] }""");
        var client = Make(handler);

        await client.ProposeAsync("the base transcript text", Context());

        var req = Assert.Single(handler.Requests);
        Assert.EndsWith(BrainApiCorrectionLlm.RoutePath, req.Uri!.AbsolutePath);
        Assert.Equal("Bearer", req.AuthScheme);
        using var doc = JsonDocument.Parse(req.Body);
        Assert.Equal("the base transcript text", doc.RootElement.GetProperty("base_text").GetString());
        Assert.Equal("Total Energies", doc.RootElement.GetProperty("glossary")[0].GetProperty("before").GetString());
        Assert.Equal("guilhem", doc.RootElement.GetProperty("participants")[0].GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Maps_well_formed_candidates()
    {
        const string baseText = "we met with Total Energies about the deal";
        var body = """
            { "candidates": [
                { "span_start": 12, "span_end": 26, "before": "Total Energies",
                  "candidates": ["TotalEnergies"], "kind": "Term", "confidence": 0.8 } ] }
            """;
        var client = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, body));

        var cands = await client.ProposeAsync(baseText, Context());

        var c = Assert.Single(cands);
        Assert.Equal("Total Energies", c.Before);
        Assert.Equal("TotalEnergies", Assert.Single(c.Candidates));
        Assert.Equal(CorrectionKind.Term, c.Kind);
        Assert.Equal(12, c.Span.Start);
    }

    [Fact]
    public async Task Drops_a_candidate_with_an_out_of_range_span_never_coerces_it()
    {
        const string baseText = "short"; // length 5
        var body = """
            { "candidates": [
                { "span_start": 0, "span_end": 99, "before": "short",
                  "candidates": ["shorter"], "kind": "Term", "confidence": 0.9 } ] }
            """;
        var client = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, body));

        var cands = await client.ProposeAsync(baseText, Context());

        Assert.Empty(cands); // out-of-range span dropped, not trusted
    }

    [Fact]
    public async Task Empty_or_missing_candidates_maps_to_an_empty_list()
    {
        var client = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        Assert.Empty(await client.ProposeAsync("t", CorrectionContext.Empty));
    }

    [Fact]
    public async Task A_5xx_is_a_retryable_correction_error()
    {
        var client = Make(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<CorrectionLlmException>(() => client.ProposeAsync("t", CorrectionContext.Empty));
        Assert.True(ex.Retryable);
    }
}
