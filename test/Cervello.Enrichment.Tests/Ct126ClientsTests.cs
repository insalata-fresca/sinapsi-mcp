using System.Net;
using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// L1 unit tests for the LIVE CT126 clients (E4's deferred adapters) over a MOCK HttpClient (no live
/// CT126, no audio). <see cref="Ct126TranscribeClient"/>: multipart request + <c>{text}</c> mapping
/// + error classification. <see cref="Ct126ReAsrClient"/>: span-scoped request + Clear/Unclear
/// mapping (below the clarity floor → Unclear, never guessed). What L2 verifies live: the real CT126
/// endpoints + the char→audio-offset resolution for re-ASR (flagged contract gap).
/// </summary>
public sealed class Ct126ClientsTests
{
    private static readonly byte[] Audio = [0x11, 0x22, 0x33];

    // ── base transcription ────────────────────────────────────────────────────
    [Fact]
    public async Task Transcribe_posts_multipart_with_language_and_maps_text()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{ "text": "bonjour le monde" }""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ct126.test") };
        var client = new Ct126TranscribeClient(http, new StaticBearerProvider("t"));

        var res = await client.TranscribeAsync(Audio, "m4a", "fr");

        var req = Assert.Single(handler.Requests);
        Assert.EndsWith(Ct126TranscribeClient.RoutePath, req.Uri!.AbsolutePath);
        Assert.Equal("Bearer", req.AuthScheme);
        Assert.StartsWith("multipart/form-data", req.ContentType);
        Assert.Contains("fr", req.Body);                    // language part present
        Assert.Contains("response_format", req.Body);       // json format part present
        Assert.Equal("bonjour le monde", res.Markdown);
        Assert.Equal("fr", res.Language);                   // language echoed onto the substrate
    }

    [Fact]
    public async Task Transcribe_5xx_is_transient_4xx_is_terminal()
    {
        var http500 = new HttpClient(StubHttpMessageHandler.Status(HttpStatusCode.BadGateway))
        { BaseAddress = new Uri("http://ct126.test") };
        await Assert.ThrowsAsync<TranscribeTransientException>(() =>
            new Ct126TranscribeClient(http500, new StaticBearerProvider("t")).TranscribeAsync(Audio, "m4a", "fr"));

        var http422 = new HttpClient(StubHttpMessageHandler.Status(HttpStatusCode.UnprocessableEntity))
        { BaseAddress = new Uri("http://ct126.test") };
        await Assert.ThrowsAsync<TranscribeTerminalException>(() =>
            new Ct126TranscribeClient(http422, new StaticBearerProvider("t")).TranscribeAsync(Audio, "m4a", "fr"));
    }

    // ── selective re-ASR ──────────────────────────────────────────────────────
    [Fact]
    public async Task ReAsr_sends_only_the_span_and_maps_a_clear_result()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{ "text": "TotalEnergies", "confidence": 0.9 }""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ct126.test") };
        var client = new Ct126ReAsrClient(http, new StaticBearerProvider("t"));

        var res = await client.ReAsrAsync("rec-1", new TextSpan(10, 24));

        var req = Assert.Single(handler.Requests);
        Assert.EndsWith(Ct126ReAsrClient.RoutePath, req.Uri!.AbsolutePath);
        Assert.Contains("\"span_start\":10", req.Body.Replace(" ", ""));
        Assert.Contains("\"span_end\":24", req.Body.Replace(" ", ""));
        Assert.True(res.Clarified);
        Assert.Equal("TotalEnergies", res.Text);
        Assert.Equal(0.9, res.Confidence, 3);
    }

    [Fact]
    public async Task ReAsr_below_the_clarity_floor_is_Unclear_never_guessed()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{ "text": "maybe?", "confidence": 0.2 }""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ct126.test") };
        var client = new Ct126ReAsrClient(http, new StaticBearerProvider("t"));

        var res = await client.ReAsrAsync("rec-1", new TextSpan(0, 5));

        Assert.False(res.Clarified);          // low confidence → no evidence → leave the span as-is
        Assert.Null(res.Text);
    }

    [Fact]
    public async Task ReAsr_empty_text_is_Unclear()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{ "text": "", "confidence": 0.99 }""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ct126.test") };
        var client = new Ct126ReAsrClient(http, new StaticBearerProvider("t"));

        var res = await client.ReAsrAsync("rec-1", new TextSpan(0, 5));
        Assert.False(res.Clarified);
    }
}
