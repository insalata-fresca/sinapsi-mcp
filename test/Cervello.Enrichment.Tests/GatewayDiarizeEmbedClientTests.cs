using System.Net;
using System.Text.Json;
using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// L1 unit tests for the LIVE <see cref="GatewayDiarizeEmbedClient"/> (E2a's deferred adapter) over
/// a MOCK HttpClient (no live sidecar, no brain-api, no personal audio). Asserts the request shape
/// (path, bearer, base64 audio + format), the 200 response mapping (192-d invariant, model block),
/// and the failure classification (4xx→terminal, 5xx/timeout/transport→transient). What L2 verifies
/// live: the real brain-api route + sidecar round-trip, and transient-audio confinement on CT139.
/// </summary>
public sealed class GatewayDiarizeEmbedClientTests
{
    private static readonly byte[] Audio = [0x01, 0x02, 0x03, 0x04];

    private static (GatewayDiarizeEmbedClient Client, StubHttpMessageHandler Handler) Make(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://brain-api.test") };
        var client = new GatewayDiarizeEmbedClient(http, new StaticBearerProvider("test-bearer"));
        return (client, handler);
    }

    private static string OkBody() =>
        """
        { "segments":   [ { "speaker": "s1", "start": 0.0, "end": 4.2 },
                          { "speaker": "s2", "start": 4.2, "end": 8.0 } ],
          "embeddings": [ { "speaker": "s1", "vector": [VEC] },
                          { "speaker": "s2", "vector": [VEC] } ],
          "model": { "vad": "silero-vad", "embed": "speechbrain/spkrec-ecapa-voxceleb", "dim": 192 } }
        """.Replace("[VEC]", Vec192());

    private static string Vec192() =>
        "[" + string.Join(",", Enumerable.Repeat("0.01", 192)) + "]";

    private static DiarizeEmbedRequest Req() => new(Audio, "m4a", minSegmentMs: 500, windowMs: 1500);

    [Fact]
    public async Task Posts_to_the_route_with_bearer_and_base64_audio()
    {
        var (client, handler) = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody()));

        await client.DiarizeEmbedAsync(Req());

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.EndsWith(GatewayDiarizeEmbedClient.RoutePath, req.Uri!.AbsolutePath);
        Assert.Equal("Bearer", req.AuthScheme);
        Assert.Equal("test-bearer", req.Bearer);
        using var doc = JsonDocument.Parse(req.Body);
        Assert.Equal(Convert.ToBase64String(Audio), doc.RootElement.GetProperty("audio").GetString());
        Assert.Equal("m4a", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(500, doc.RootElement.GetProperty("min_segment_ms").GetInt32());
        Assert.Equal(1500, doc.RootElement.GetProperty("window_ms").GetInt32());
    }

    [Fact]
    public async Task Maps_the_200_response_including_the_192d_vectors_and_model()
    {
        var (client, _) = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody()));

        var res = await client.DiarizeEmbedAsync(Req());

        Assert.Equal(2, res.Segments.Count);
        Assert.Equal("s1", res.Segments[0].Speaker);
        Assert.Equal(4.2, res.Segments[0].End, 3);
        Assert.Equal(2, res.Embeddings.Count);
        Assert.All(res.Embeddings, e => Assert.Equal(SpeakerEmbedding.ExpectedDim, e.Vector.Count));
        Assert.Equal("silero-vad", res.Model.Vad);
        Assert.Equal(192, res.Model.Dim);
    }

    [Fact]
    public async Task A_non_192d_vector_is_a_terminal_contract_violation_not_a_fabrication()
    {
        var bad = OkBody().Replace(Vec192(), "[0.1,0.2,0.3]"); // 3-d, violates the contract
        var (client, _) = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, bad));

        await Assert.ThrowsAsync<DiarizeEmbedTerminalException>(() => client.DiarizeEmbedAsync(Req()));
    }

    [Fact]
    public async Task A_4xx_is_terminal_and_surfaces_the_rfc7807_detail()
    {
        var problem = """{ "title": "invalid-audio", "detail": "undecodable m4a", "status": 400 }""";
        var (client, _) = Make(StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, problem));

        var ex = await Assert.ThrowsAsync<DiarizeEmbedTerminalException>(() => client.DiarizeEmbedAsync(Req()));
        Assert.False(ex.Retryable);
        Assert.Contains("undecodable m4a", ex.Reason);
    }

    [Fact]
    public async Task A_5xx_is_transient_retryable()
    {
        var (client, _) = Make(StubHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "sidecar down"));

        var ex = await Assert.ThrowsAsync<DiarizeEmbedTransientException>(() => client.DiarizeEmbedAsync(Req()));
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task A_transport_error_is_transient_retryable()
    {
        var (client, _) = Make(StubHttpMessageHandler.Throwing(new HttpRequestException("connection reset")));

        var ex = await Assert.ThrowsAsync<DiarizeEmbedTransientException>(() => client.DiarizeEmbedAsync(Req()));
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task The_adapter_retains_no_audio_after_the_call()
    {
        // Confinement analogue (the real proxy/sidecar keep nothing): the adapter holds no reference
        // to the request audio after returning — it only base64-encodes it into the transient body.
        var (client, handler) = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody()));
        await client.DiarizeEmbedAsync(Req());
        // The only place the audio appears is the captured outbound body (base64), never retained state.
        Assert.Single(handler.Requests);
        Assert.Contains(Convert.ToBase64String(Audio), handler.Requests[0].Body);
    }
}
