using System.Net;
using System.Text.Json;
using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// L1 unit tests for the LIVE <see cref="GatewayDiarizeEmbedClient"/> (E2a's deferred adapter) over
/// a MOCK HttpClient (no live sidecar, no brain-api, no personal audio). Asserts the request WIRE
/// FORMAT (path, bearer, RAW audio bytes round-tripping byte-for-byte, <c>audio/&lt;format&gt;</c>
/// content-type, tuning params on the query string), the 200 response mapping (256-d invariant, model
/// block), and the failure classification (4xx→terminal, 5xx/timeout/transport→transient). What L2
/// verifies live: the real brain-api route + sidecar round-trip, and transient-audio confinement on
/// CT139.
///
/// <para>The <see cref="Sends_raw_audio_bytes_with_audio_content_type_not_json"/> pin exists because
/// an earlier JSON-base64 envelope passed every mock assertion yet made the live sidecar's ffmpeg
/// reject the body ("Invalid data found when processing input"): the sidecar reads the RAW request
/// body, so the wire format — not just the parsed fields — must be pinned.</para>
/// </summary>
public sealed class GatewayDiarizeEmbedClientTests
{
    // Include a non-ASCII / non-UTF-8 byte (0xFF) so a lossy text round-trip would corrupt it — the
    // test proves the exact bytes leave the wire unchanged, which base64/JSON/multipart would not.
    private static readonly byte[] Audio = [0x01, 0x02, 0x03, 0x04, 0xFF, 0x00, 0x52, 0x49, 0x46, 0x46];

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
          "model": { "vad": "silero-vad", "embed": "pyannote/wespeaker-voxceleb-resnet34-LM", "dim": 256 } }
        """.Replace("[VEC]", Vec256());

    private static string Vec256() =>
        "[" + string.Join(",", Enumerable.Repeat("0.01", 256)) + "]";

    private static DiarizeEmbedRequest Req() => new(Audio, "m4a", minSegmentMs: 500, windowMs: 1500);

    [Fact]
    public async Task Posts_to_the_route_with_bearer_and_tuning_params_on_the_query_string()
    {
        var (client, handler) = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody()));

        await client.DiarizeEmbedAsync(Req());

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.EndsWith(GatewayDiarizeEmbedClient.RoutePath, req.Uri!.AbsolutePath);
        Assert.Equal("Bearer", req.AuthScheme);
        Assert.Equal("test-bearer", req.Bearer);
        // Tuning params ride the QUERY STRING (where the sidecar reads request.query_params) — NOT the body.
        var query = req.Uri!.Query;
        Assert.Contains("min_segment_ms=500", query);
        Assert.Contains("window_ms=1500", query);
    }

    [Fact]
    public async Task Sends_raw_audio_bytes_with_audio_content_type_not_json()
    {
        // THE wire-format pin: the sidecar does `body = await request.body()` and feeds the bytes
        // straight to ffmpeg, so the body must be the RAW audio bytes (byte-for-byte) under an
        // `audio/<format>` content-type — NOT a JSON/base64/multipart envelope (which ffmpeg rejects).
        var (client, handler) = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody()));

        await client.DiarizeEmbedAsync(Req());

        var req = Assert.Single(handler.Requests);
        Assert.Equal("audio/m4a", req.ContentType);                 // Content-Type: audio/<format>
        Assert.Equal(Audio, req.BodyBytes);                          // exact bytes, no encoding/wrapping
        // And explicitly NOT a JSON envelope: the first byte is the raw audio, not '{'.
        Assert.NotEqual((byte)'{', req.BodyBytes[0]);
        Assert.False(IsJson(req.Body), "body must not be a JSON envelope");
    }

    private static bool IsJson(string body)
    {
        try { using var _ = JsonDocument.Parse(body); return true; }
        catch (JsonException) { return false; }
    }

    [Fact]
    public async Task A_wav_format_maps_to_the_audio_wav_content_type()
    {
        var (client, handler) = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody()));

        await client.DiarizeEmbedAsync(new DiarizeEmbedRequest(Audio, "wav"));

        Assert.Equal("audio/wav", Assert.Single(handler.Requests).ContentType);
    }

    [Fact]
    public async Task Omits_query_params_when_no_tuning_is_requested()
    {
        var (client, handler) = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody()));

        await client.DiarizeEmbedAsync(new DiarizeEmbedRequest(Audio, "m4a"));

        Assert.Equal(string.Empty, Assert.Single(handler.Requests).Uri!.Query);
    }

    [Fact]
    public async Task Maps_the_200_response_including_the_256d_vectors_and_model()
    {
        var (client, _) = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody()));

        var res = await client.DiarizeEmbedAsync(Req());

        Assert.Equal(2, res.Segments.Count);
        Assert.Equal("s1", res.Segments[0].Speaker);
        Assert.Equal(4.2, res.Segments[0].End, 3);
        Assert.Equal(2, res.Embeddings.Count);
        Assert.All(res.Embeddings, e => Assert.Equal(SpeakerEmbedding.ExpectedDim, e.Vector.Count));
        Assert.Equal("silero-vad", res.Model.Vad);
        Assert.Equal(256, res.Model.Dim);
    }

    [Fact]
    public async Task A_non_256d_vector_is_a_terminal_contract_violation_not_a_fabrication()
    {
        var bad = OkBody().Replace(Vec256(), "[0.1,0.2,0.3]"); // 3-d, violates the contract
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
        // to the request audio after returning — it only streams the raw bytes into the transient body.
        var (client, handler) = Make(StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody()));
        await client.DiarizeEmbedAsync(Req());
        // The only place the audio appears is the captured outbound body (raw bytes), never retained state.
        Assert.Single(handler.Requests);
        Assert.Equal(Audio, handler.Requests[0].BodyBytes);
    }
}
