using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IReAsrClient"/> over CT126 speaches (<c>:8000</c>) for SELECTIVE re-ASR of a
/// single garbled span (spec <c>text-correction</c> → "Selective re-ASR for garbled spans only").
/// The <c>CorrectionStage</c> calls this ONLY for spans the base marks garbled — never the whole
/// transcript. A clarified result becomes EVIDENCE for a <see cref="CorrectionKind.Garbled"/> diff,
/// graded by the decision policy; an unclear result yields <see cref="ReAsrResult.Unclear"/> (the
/// span is left as-is, never guessed). Bearer-gated via <see cref="IBearerProvider"/> (agent-free).
///
/// <para><b>L2 CONTRACT NOTE (flagged for live verification):</b> the <see cref="IReAsrClient"/>
/// port passes a <see cref="TextSpan"/> of CHARACTER offsets in the base transcript, whereas CT126
/// re-ASR operates on an AUDIO time window. The char→audio-offset resolution requires the base
/// transcript's word-level timestamps (CT146-side, from base transcription). This adapter posts the
/// recording id + span offsets to the CT126 re-ASR endpoint; the char→time mapping is an L2
/// live-integration concern (either the span endpoint accepts char offsets + the CT-side word map,
/// or the offsets are pre-resolved to seconds before the call). Built to the port contract; the
/// offset semantics are an L2 STOP-review item (see the mission return).</para>
/// </summary>
public sealed class Ct126ReAsrClient : IReAsrClient
{
    /// <summary>The CT126 selective re-ASR route (span-scoped, NOT the whole-file transcription route).</summary>
    public const string RoutePath = "/v1/audio/reasr";

    /// <summary>The logical bearer audience for CT126 egress.</summary>
    public const string Audience = "ct126-speaches";

    /// <summary>Below this confidence a re-ASR result is treated as "did not clarify" (→ Unclear).</summary>
    public const double ClarityFloor = 0.5;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IBearerProvider _bearer;
    private readonly ILogger _log;

    public Ct126ReAsrClient(HttpClient http, IBearerProvider bearer, ILogger<Ct126ReAsrClient>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _bearer = bearer ?? throw new ArgumentNullException(nameof(bearer));
        _log = log ?? NullLogger<Ct126ReAsrClient>.Instance;
    }

    public async Task<ReAsrResult> ReAsrAsync(string recordingId, TextSpan span, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        ArgumentNullException.ThrowIfNull(span);

        var body = new WireRequest(recordingId, span.Start, span.End);
        using var req = new HttpRequestMessage(HttpMethod.Post, RoutePath)
        {
            Content = JsonContent.Create(body, options: _json),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _bearer.GetBearerAsync(Audience, ct).ConfigureAwait(false));

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new TranscribeTransientException("CT126 re-ASR timed out", e);
        }
        catch (HttpRequestException e)
        {
            throw new TranscribeTransientException($"CT126 re-ASR transport error: {e.Message}", e);
        }

        using (res)
        {
            if (res.IsSuccessStatusCode)
            {
                var wire = await res.Content.ReadFromJsonAsync<WireResponse>(_json, ct).ConfigureAwait(false);
                // No text, or below the clarity floor → the span did not clarify: leave as-is, never guess.
                if (wire?.Text is not { Length: > 0 } || wire.Confidence < ClarityFloor)
                    return ReAsrResult.Unclear;
                return ReAsrResult.Clear(wire.Text, System.Math.Clamp(wire.Confidence, 0.0, 1.0));
            }

            var reason = res.ReasonPhrase ?? "no detail";
            var code = (int)res.StatusCode;
            if (res.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout)
                throw new TranscribeTransientException($"CT126 re-ASR {code}: {reason}");
            throw new TranscribeTerminalException($"CT126 re-ASR {code}: {reason}");
        }
    }

    private sealed record WireRequest(
        [property: JsonPropertyName("recording_id")] string RecordingId,
        [property: JsonPropertyName("span_start")] int SpanStart,
        [property: JsonPropertyName("span_end")] int SpanEnd);

    private sealed record WireResponse(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("confidence")] double Confidence);
}
