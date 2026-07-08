using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IDiarizeEmbedClient"/> over the brain-api (CT139) diarize-embed proxy route
/// <c>POST /v1/enrich/diarize-embed</c> (spec <c>diarize-embed-sidecar</c>). The brain-api reverse-
/// proxies to the co-located Python sidecar (<c>127.0.0.1:8500</c>); this adapter is the engine
/// (CT146) side of the CT146→CT121→CT139 egress. Bearer-gated: the token is minted at runtime via
/// <see cref="IBearerProvider"/> (agent-free), never from agent context.
///
/// <para><b>Wire contract (byte-for-byte with the spec + E2a server):</b> request body is JSON
/// <c>{ audio: base64, format, min_segment_ms?, window_ms? }</c>; the 200 response is
/// <c>{ segments:[{speaker,start,end}], embeddings:[{speaker,vector[192]}], model:{vad,embed,dim} }</c>.
/// The engine maps this onto the strongly-typed <see cref="DiarizeEmbedResponse"/> (which enforces
/// the 192-d invariant in <see cref="SpeakerEmbedding"/>'s ctor).</para>
///
/// <para><b>Confinement:</b> audio flows out as a transient request payload only; the adapter
/// retains nothing after the call returns. Only the derived segments + embeddings are returned.</para>
///
/// <para><b>Failure classification (SCHEMAS §5):</b> timeout / 5xx / connection-reset →
/// <see cref="DiarizeEmbedTransientException"/> (retry under the same key → <c>failed_retryable</c>);
/// 4xx contract violation / undecodable audio → <see cref="DiarizeEmbedTerminalException"/>
/// (→ <c>failed_terminal</c> with reason). The rfc7807 <c>detail</c> is surfaced as the reason when
/// present. The adapter NEVER fabricates segments/embeddings on failure.</para>
/// </summary>
public sealed class GatewayDiarizeEmbedClient : IDiarizeEmbedClient
{
    /// <summary>The brain-api route path (relative to <see cref="EnrichmentConfig.BrainApiBaseUrl"/>).</summary>
    public const string RoutePath = "/v1/enrich/diarize-embed";

    /// <summary>The logical bearer audience for brain-api egress.</summary>
    public const string Audience = "brain-api";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IBearerProvider _bearer;
    private readonly ILogger _log;

    public GatewayDiarizeEmbedClient(HttpClient http, IBearerProvider bearer, ILogger<GatewayDiarizeEmbedClient>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _bearer = bearer ?? throw new ArgumentNullException(nameof(bearer));
        _log = log ?? NullLogger<GatewayDiarizeEmbedClient>.Instance;
    }

    public async Task<DiarizeEmbedResponse> DiarizeEmbedAsync(DiarizeEmbedRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new WireRequest(
            Audio: Convert.ToBase64String(request.Audio.Span),
            Format: request.Format,
            MinSegmentMs: request.MinSegmentMs,
            WindowMs: request.WindowMs);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, RoutePath)
        {
            Content = JsonContent.Create(body, options: _json),
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _bearer.GetBearerAsync(Audience, ct).ConfigureAwait(false));

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout surfaces as a TaskCanceledException with no external cancellation.
            throw new DiarizeEmbedTransientException("diarize-embed request timed out", e);
        }
        catch (HttpRequestException e)
        {
            // Connection reset / DNS / socket — transient at the network layer.
            throw new DiarizeEmbedTransientException($"diarize-embed transport error: {e.Message}", e);
        }

        using (res)
        {
            if (res.IsSuccessStatusCode)
            {
                var wire = await ReadWireAsync(res, ct).ConfigureAwait(false);
                return Map(wire);
            }

            var reason = await ProblemReasonAsync(res, ct).ConfigureAwait(false);
            var code = (int)res.StatusCode;
            if (res.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout)
                throw new DiarizeEmbedTransientException($"diarize-embed {code}: {reason}");
            // Any other 4xx is a terminal contract violation (invalid/undecodable audio, bad request).
            throw new DiarizeEmbedTerminalException($"diarize-embed {code}: {reason}");
        }
    }

    private static async Task<WireResponse> ReadWireAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var wire = await res.Content.ReadFromJsonAsync<WireResponse>(_json, ct).ConfigureAwait(false);
        if (wire is null)
            throw new DiarizeEmbedTerminalException("diarize-embed 200 with an empty/unparseable body");
        return wire;
    }

    /// <summary>Map the wire JSON onto the domain response (the 192-d invariant is enforced by the ctors).</summary>
    private static DiarizeEmbedResponse Map(WireResponse wire)
    {
        if (wire.Segments is null || wire.Embeddings is null || wire.Model is null)
            throw new DiarizeEmbedTerminalException("diarize-embed 200 missing segments/embeddings/model");
        try
        {
            var segments = wire.Segments.Select(s => new DiarizedSegment(s.Speaker!, s.Start, s.End)).ToList();
            var embeddings = wire.Embeddings.Select(e => new SpeakerEmbedding(e.Speaker!, e.Vector!)).ToList();
            var model = new DiarizeEmbedModel(wire.Model.Vad!, wire.Model.Embed!, wire.Model.Dim);
            return new DiarizeEmbedResponse(segments, embeddings, model);
        }
        catch (ArgumentException e)
        {
            // A contract-shape violation from the server (e.g. a non-192-d vector) is terminal — the
            // engine never fabricates; it surfaces the exact reason for failed_terminal.
            throw new DiarizeEmbedTerminalException($"diarize-embed response violates the contract: {e.Message}", e);
        }
    }

    /// <summary>Extract the rfc7807 <c>detail</c> (or <c>title</c>) as the failure reason, best-effort.</summary>
    private static async Task<string> ProblemReasonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return res.ReasonPhrase ?? "no detail";
            try
            {
                var problem = JsonSerializer.Deserialize<ProblemDetails>(text, _json);
                var detail = problem?.Detail ?? problem?.Title;
                return string.IsNullOrWhiteSpace(detail) ? Truncate(text) : detail;
            }
            catch (JsonException)
            {
                return Truncate(text);
            }
        }
        catch
        {
            return res.ReasonPhrase ?? "no detail";
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];

    // ── wire DTOs (mirror the spec's JSON shape exactly) ────────────────────────
    private sealed record WireRequest(
        [property: JsonPropertyName("audio")] string Audio,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("min_segment_ms")] int? MinSegmentMs,
        [property: JsonPropertyName("window_ms")] int? WindowMs);

    private sealed record WireResponse(
        [property: JsonPropertyName("segments")] List<WireSegment>? Segments,
        [property: JsonPropertyName("embeddings")] List<WireEmbedding>? Embeddings,
        [property: JsonPropertyName("model")] WireModel? Model);

    private sealed record WireSegment(
        [property: JsonPropertyName("speaker")] string? Speaker,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End);

    private sealed record WireEmbedding(
        [property: JsonPropertyName("speaker")] string? Speaker,
        [property: JsonPropertyName("vector")] float[]? Vector);

    private sealed record WireModel(
        [property: JsonPropertyName("vad")] string? Vad,
        [property: JsonPropertyName("embed")] string? Embed,
        [property: JsonPropertyName("dim")] int Dim);

    private sealed record ProblemDetails(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("status")] int? Status);
}
