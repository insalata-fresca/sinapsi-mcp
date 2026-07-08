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
/// Live <see cref="ICorrectionLlm"/> over the brain-api (CT139) correction route. The engine hands
/// the LLM the base transcript + the historized glossary + the resolved participants and it PROPOSES
/// <see cref="CorrectionCandidate"/>s — spans it thinks are wrong plus the resolution(s) it
/// considered. It NEVER returns a rewritten transcript (spec <c>text-correction</c> → "diffs not
/// rewrites"). Bearer-gated via <see cref="IBearerProvider"/> (agent-free).
///
/// <para><b>Ungated proposal, gated write.</b> The candidates this adapter returns are UNGATED — the
/// <c>CorrectionStage</c> + <c>CorrectionGrader</c> evidence-gate each one against the glossary /
/// participants / re-ASR before it can become a <see cref="CorrectionDiff"/>. So a candidate the LLM
/// invents with no backing resolves to OMIT downstream; this adapter just faithfully transports the
/// proposal (the no-fabrication floor lives in the stage, by design).</para>
/// </summary>
public sealed class BrainApiCorrectionLlm : ICorrectionLlm
{
    /// <summary>The brain-api correction route (relative to <see cref="EnrichmentConfig.BrainApiBaseUrl"/>).</summary>
    public const string RoutePath = "/v1/enrich/correct";

    /// <summary>The logical bearer audience for brain-api egress.</summary>
    public const string Audience = "brain-api";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IBearerProvider _bearer;
    private readonly ILogger _log;

    public BrainApiCorrectionLlm(HttpClient http, IBearerProvider bearer, ILogger<BrainApiCorrectionLlm>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _bearer = bearer ?? throw new ArgumentNullException(nameof(bearer));
        _log = log ?? NullLogger<BrainApiCorrectionLlm>.Instance;
    }

    public async Task<IReadOnlyList<CorrectionCandidate>> ProposeAsync(
        string baseText, CorrectionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseText);
        ArgumentNullException.ThrowIfNull(context);

        var body = new WireRequest(
            BaseText: baseText,
            Glossary: context.Glossary.Select(g => new WireGlossary(g.Before, g.After, g.Kind.ToString())).ToList(),
            Participants: context.Participants
                .Select(p => new WireParticipant(p.Slug, p.CanonicalName, p.Aliases.ToList())).ToList());

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
            throw new CorrectionLlmException("correction LLM request timed out", retryable: true, e);
        }
        catch (HttpRequestException e)
        {
            throw new CorrectionLlmException($"correction LLM transport error: {e.Message}", retryable: true, e);
        }

        using (res)
        {
            if (res.IsSuccessStatusCode)
            {
                var wire = await res.Content.ReadFromJsonAsync<WireResponse>(_json, ct).ConfigureAwait(false);
                return Map(wire, baseText);
            }

            var reason = res.ReasonPhrase ?? "no detail";
            var code = (int)res.StatusCode;
            var retryable = res.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout;
            throw new CorrectionLlmException($"correction LLM {code}: {reason}", retryable);
        }
    }

    /// <summary>Map the wire candidates onto domain <see cref="CorrectionCandidate"/>s (ctors validate shape).</summary>
    private static IReadOnlyList<CorrectionCandidate> Map(WireResponse? wire, string baseText)
    {
        if (wire?.Candidates is null || wire.Candidates.Count == 0)
            return Array.Empty<CorrectionCandidate>();

        var outp = new List<CorrectionCandidate>(wire.Candidates.Count);
        foreach (var c in wire.Candidates)
        {
            if (c.Candidates is not { Count: > 0 } || string.IsNullOrEmpty(c.Before))
                continue; // a malformed candidate is dropped, never coerced into a phantom correction
            // Clamp span end into the transcript so a server off-by-one can't crash the stage;
            // an out-of-range span is dropped rather than trusted.
            if (c.SpanStart < 0 || c.SpanEnd <= c.SpanStart || c.SpanEnd > baseText.Length)
                continue;
            var kind = ParseKind(c.Kind);
            outp.Add(new CorrectionCandidate(
                new TextSpan(c.SpanStart, c.SpanEnd),
                c.Before,
                c.Candidates,
                kind,
                System.Math.Clamp(c.Confidence, 0.0, 1.0)));
        }
        return outp;
    }

    private static CorrectionKind ParseKind(string? kind) =>
        Enum.TryParse<CorrectionKind>(kind, ignoreCase: true, out var k) ? k : CorrectionKind.Term;

    // ── wire DTOs ────────────────────────────────────────────────────────────────
    private sealed record WireRequest(
        [property: JsonPropertyName("base_text")] string BaseText,
        [property: JsonPropertyName("glossary")] List<WireGlossary> Glossary,
        [property: JsonPropertyName("participants")] List<WireParticipant> Participants);

    private sealed record WireGlossary(
        [property: JsonPropertyName("before")] string Before,
        [property: JsonPropertyName("after")] string After,
        [property: JsonPropertyName("kind")] string Kind);

    private sealed record WireParticipant(
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("canonical_name")] string CanonicalName,
        [property: JsonPropertyName("aliases")] List<string> Aliases);

    private sealed record WireResponse(
        [property: JsonPropertyName("candidates")] List<WireCandidate>? Candidates);

    private sealed record WireCandidate(
        [property: JsonPropertyName("span_start")] int SpanStart,
        [property: JsonPropertyName("span_end")] int SpanEnd,
        [property: JsonPropertyName("before")] string? Before,
        [property: JsonPropertyName("candidates")] List<string>? Candidates,
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("confidence")] double Confidence);
}

/// <summary>A correction-LLM call failure carrying whether it is retryable (maps onto SCHEMAS §5).</summary>
public sealed class CorrectionLlmException(string reason, bool retryable, Exception? inner = null)
    : Exception(reason, inner)
{
    public string Reason { get; } = reason;
    public bool Retryable { get; } = retryable;
}
