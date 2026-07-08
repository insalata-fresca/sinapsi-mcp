using System.Net;
using System.Text.Json;
using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// L1 unit tests for the LIVE <see cref="BrainApiRecordingFactSource"/> over a MOCK HttpClient (no
/// live brain-api). Mirrors <see cref="BrainApiCorrectionLlmTests"/>. Asserts the request carries the
/// base transcript (the grounding substrate) + bearer, the well-formed fact mapping, and — the
/// load-bearing part — the NEVER-GUESS floor enforced at the wire→domain boundary: an unsourced
/// timeline line, a malformed link, and a bad garbled span are DROPPED (never invented/coerced), and
/// an unknown attention verdict falls back to the conservative <c>ping</c> (never auto-promote).
/// </summary>
public sealed class BrainApiRecordingFactSourceTests
{
    private static BrainApiRecordingFactSource Make(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://brain-api.test") }, new StaticBearerProvider("t"));

    private static BaseTranscript Base() => new("le standup de ce matin", "fr");

    [Fact]
    public async Task Sends_the_base_transcript_and_bearer_to_the_derive_route()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        var client = Make(handler);

        await client.GetFactsAsync("20260704-standup", Base());

        var req = Assert.Single(handler.Requests);
        Assert.EndsWith(BrainApiRecordingFactSource.RoutePath, req.Uri!.AbsolutePath);
        Assert.Equal("Bearer", req.AuthScheme);
        using var doc = JsonDocument.Parse(req.Body);
        Assert.Equal("20260704-standup", doc.RootElement.GetProperty("recording_id").GetString());
        Assert.Equal("le standup de ce matin", doc.RootElement.GetProperty("base_text").GetString());
        Assert.Equal("fr", doc.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public async Task Maps_well_formed_facts()
    {
        var body = """
            {
              "summary": "morning standup",
              "entities": ["TotalEnergies"],
              "dates": ["2026-07-04"],
              "links": [ { "target": "[[guilhem]]", "confidence": 0.7 } ],
              "timeline": [ { "date": "2026-07-04", "fact": "kickoff", "source": "rec://20260704-standup#s1" } ],
              "attention": { "verdict": "promote", "score": 0.8, "reason": "actionable" },
              "participants": [ { "slug": "guilhem", "canonical_name": "Guilhem", "aliases": ["Guilhome"] } ],
              "garbled_spans": [ { "start": 3, "end": 9 } ]
            }
            """;
        var client = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, body));

        var facts = await client.GetFactsAsync("20260704-standup", Base());

        Assert.Equal("morning standup", facts.Summary);
        Assert.Equal("TotalEnergies", Assert.Single(facts.Entities));
        Assert.Equal("[[guilhem]]", Assert.Single(facts.ProposedLinks).Target);
        var t = Assert.Single(facts.ProposedTimeline);
        Assert.Equal("rec://20260704-standup#s1", t.Source);
        Assert.Equal("promote", facts.Attention.Verdict);
        Assert.Equal("guilhem", Assert.Single(facts.Participants).Slug);
        var span = Assert.Single(facts.GarbledSpans);
        Assert.Equal(3, span.Start);
        Assert.Equal(9, span.End);
    }

    [Fact]
    public async Task Drops_an_unsourced_timeline_line_never_invents_a_source()
    {
        var body = """
            { "timeline": [
                { "date": "2026-07-04", "fact": "kickoff", "source": "" },
                { "date": "2026-07-04", "fact": "grounded", "source": "rec://r#s1" } ] }
            """;
        var client = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, body));

        var facts = await client.GetFactsAsync("r", Base());

        // Only the sourced line survives — the unsourced one is dropped, not back-filled.
        var t = Assert.Single(facts.ProposedTimeline);
        Assert.Equal("grounded", t.Fact);
    }

    [Fact]
    public async Task Drops_a_malformed_link_never_coerces_it()
    {
        var body = """
            { "links": [
                { "target": "guilhem", "confidence": 0.7 },
                { "target": "[[ok]]", "confidence": 0.5 },
                { "target": "[[bad]]", "confidence": 5.0 } ] }
            """;
        var client = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, body));

        var facts = await client.GetFactsAsync("r", Base());

        // Only the well-formed [[slug]] with in-range confidence survives.
        Assert.Equal("[[ok]]", Assert.Single(facts.ProposedLinks).Target);
    }

    [Fact]
    public async Task Drops_an_invalid_garbled_span_no_phantom_reasr()
    {
        var body = """{ "garbled_spans": [ { "start": 5, "end": 2 }, { "start": 0, "end": 4 } ] }""";
        var client = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, body));

        var facts = await client.GetFactsAsync("r", Base());

        var span = Assert.Single(facts.GarbledSpans);
        Assert.Equal(0, span.Start);
        Assert.Equal(4, span.End);
    }

    [Fact]
    public async Task Unknown_attention_verdict_falls_back_to_conservative_ping()
    {
        var body = """{ "attention": { "verdict": "auto-promote-everything", "score": 0.99, "reason": "x" } }""";
        var client = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, body));

        var facts = await client.GetFactsAsync("r", Base());

        Assert.Equal("ping", facts.Attention.Verdict); // never auto-promote on an unrecognised verdict
    }

    [Fact]
    public async Task Empty_body_is_a_conservative_empty_fact_set()
    {
        var client = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));

        var facts = await client.GetFactsAsync("r", Base());

        Assert.Empty(facts.ProposedTimeline);
        Assert.Empty(facts.ProposedLinks);
        Assert.Equal("ping", facts.Attention.Verdict);
    }

    [Fact]
    public async Task A_5xx_is_a_retryable_derivation_error()
    {
        var client = Make(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<RecordingFactSourceException>(() => client.GetFactsAsync("r", Base()));
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task A_malformed_body_is_a_terminal_derivation_error_no_fabrication()
    {
        var client = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, "this is not json"));

        var ex = await Assert.ThrowsAsync<RecordingFactSourceException>(() => client.GetFactsAsync("r", Base()));
        Assert.False(ex.Retryable); // garbage in → refuse to derive, do not guess
    }
}
