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
/// Live <see cref="IRecordingFactSource"/> over the brain-api (CT139) recording-derivation route —
/// mirrors <see cref="BrainApiCorrectionLlm"/> exactly (typed HttpClient, bearer-gated via
/// <see cref="IBearerProvider"/>, transient-vs-terminal transport classification). The engine hands
/// the LLM the base transcript + the map-derived participant roster and it PROPOSES the derived,
/// non-attribution facts a bundle needs: a summary, entities/dates, candidate <c>[[link]]</c>s,
/// timeline candidates, the attention verdict, the resolved participants, and the garbled spans (the
/// selective-re-ASR candidates).
///
/// <para><b>Evidence-gated — every proposed fact carries a <c>source:</c> ref, unbacked → omitted,
/// never invented (the hard never-guess floor, DESIGN §5.1).</b> This adapter enforces the floor at the
/// wire→domain boundary, NOT downstream:
/// <list type="bullet">
///   <item>A proposed <b>timeline line</b> with no non-empty <c>source</c> ref is DROPPED (it never
///     constructs a <see cref="ProposedTimelineLine"/> — whose ctor also refuses an unsourced line, R1).</item>
///   <item>A proposed <b>link</b> that is not a well-formed <c>[[slug]]</c> wikilink, or whose
///     confidence is out of range, is DROPPED (never coerced into a phantom link).</item>
///   <item>A <b>garbled span</b> with an invalid/empty range is DROPPED (a bad span never triggers a
///     phantom re-ASR).</item>
///   <item>The <b>attention verdict</b> is clamped to the SCHEMAS §6 vocabulary; an unknown/absent
///     verdict falls back to the CONSERVATIVE <c>ping</c> (surface for a human) rather than
///     <c>promote</c> — the engine never auto-promotes on an ungrounded signal.</item>
/// </list>
/// The attribution is NOT part of this source (the attribution stage computes it from voiceprints);
/// participants here only GROUND the correction stage. Conservative output is fine — the pipeline
/// escalates/omits anything ungrounded downstream.</para>
/// </summary>
public sealed class BrainApiRecordingFactSource : IRecordingFactSource
{
    /// <summary>The brain-api recording-derivation route (relative to <see cref="EnrichmentConfig.BrainApiBaseUrl"/>).</summary>
    public const string RoutePath = "/v1/enrich/derive-facts";

    /// <summary>The logical bearer audience for brain-api egress (same as the correction LLM).</summary>
    public const string Audience = "brain-api";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IBearerProvider _bearer;
    private readonly ILogger _log;

    public BrainApiRecordingFactSource(HttpClient http, IBearerProvider bearer, ILogger<BrainApiRecordingFactSource>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _bearer = bearer ?? throw new ArgumentNullException(nameof(bearer));
        _log = log ?? NullLogger<BrainApiRecordingFactSource>.Instance;
    }

    public async Task<RecordingFacts> GetFactsAsync(
        string recordingId, BaseTranscript baseTranscript, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        ArgumentNullException.ThrowIfNull(baseTranscript);

        var body = new WireRequest(recordingId, baseTranscript.Markdown, baseTranscript.Language);

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
            throw new RecordingFactSourceException("recording-fact derivation timed out", retryable: true, e);
        }
        catch (HttpRequestException e)
        {
            throw new RecordingFactSourceException($"recording-fact derivation transport error: {e.Message}", retryable: true, e);
        }

        using (res)
        {
            if (res.IsSuccessStatusCode)
            {
                WireResponse? wire;
                try
                {
                    wire = await res.Content.ReadFromJsonAsync<WireResponse>(_json, ct).ConfigureAwait(false);
                }
                catch (JsonException e)
                {
                    // A malformed derivation response is a terminal contract violation — the engine
                    // must not fabricate facts from garbage. (The orchestrator maps a throw here to
                    // failed_retryable by default; a non-retryable signal is carried explicitly.)
                    throw new RecordingFactSourceException(
                        $"recording-fact derivation returned a malformed body: {e.Message}", retryable: false, e);
                }
                return Map(wire);
            }

            var reason = res.ReasonPhrase ?? "no detail";
            var code = (int)res.StatusCode;
            var retryable = res.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout;
            throw new RecordingFactSourceException($"recording-fact derivation {code}: {reason}", retryable);
        }
    }

    /// <summary>
    /// Map the wire facts onto <see cref="RecordingFacts"/>, DROPPING every fact that fails the
    /// never-guess floor (unsourced timeline / malformed link / bad span). Nothing is coerced into a
    /// phantom fact; an unbacked fact simply does not appear.
    /// </summary>
    private RecordingFacts Map(WireResponse? wire)
    {
        if (wire is null)
            // A null body (204/empty) → an empty, conservative fact set: nothing proposed, ping attention.
            return Empty();

        var timeline = new List<ProposedTimelineLine>();
        foreach (var t in wire.Timeline ?? [])
        {
            // The floor: no source ⇒ dropped (never invented). The ctor also enforces R1.
            if (string.IsNullOrWhiteSpace(t.Date) || string.IsNullOrWhiteSpace(t.Fact) || string.IsNullOrWhiteSpace(t.Source))
            {
                _log.LogDebug("derive-facts: dropped an unsourced/incomplete timeline candidate (never guessed)");
                continue;
            }
            timeline.Add(new ProposedTimelineLine(t.Date, t.Fact, t.Source));
        }

        var links = new List<ProposedLink>();
        foreach (var l in wire.Links ?? [])
        {
            if (string.IsNullOrWhiteSpace(l.Target)
                || !(l.Target.StartsWith("[[", StringComparison.Ordinal) && l.Target.EndsWith("]]", StringComparison.Ordinal))
                || l.Confidence is < 0.0 or > 1.0)
            {
                _log.LogDebug("derive-facts: dropped a malformed link candidate '{Target}' (never coerced)", l.Target);
                continue;
            }
            links.Add(new ProposedLink(l.Target, l.Confidence));
        }

        var garbled = new List<TextSpan>();
        foreach (var s in wire.GarbledSpans ?? [])
        {
            if (s.Start < 0 || s.End <= s.Start)
            {
                _log.LogDebug("derive-facts: dropped an invalid garbled span [{Start},{End}) (no phantom re-ASR)", s.Start, s.End);
                continue;
            }
            garbled.Add(new TextSpan(s.Start, s.End));
        }

        var participants = new List<ResolvedParticipant>();
        foreach (var p in wire.Participants ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.Slug) || string.IsNullOrWhiteSpace(p.CanonicalName))
                continue; // a participant with no slug/name grounds nothing — dropped
            participants.Add(new ResolvedParticipant(p.Slug, p.CanonicalName, p.Aliases ?? []));
        }

        return new RecordingFacts(
            summary: wire.Summary ?? "",
            entities: Clean(wire.Entities),
            dates: Clean(wire.Dates),
            proposedLinks: links,
            proposedTimeline: timeline,
            attention: MapAttention(wire.Attention),
            participants: participants,
            garbledSpans: garbled);
    }

    /// <summary>Conservative empty fact set: nothing proposed, attention defaults to <c>ping</c> (surface, don't auto-promote).</summary>
    private static RecordingFacts Empty() => new(
        summary: "",
        entities: Array.Empty<string>(),
        dates: Array.Empty<string>(),
        proposedLinks: Array.Empty<ProposedLink>(),
        proposedTimeline: Array.Empty<ProposedTimelineLine>(),
        attention: new BundleAttention("ping", 0.0, "no derived facts"),
        participants: Array.Empty<ResolvedParticipant>(),
        garbledSpans: Array.Empty<TextSpan>());

    /// <summary>Map the wire attention to the SCHEMAS §6 vocabulary; an unknown/absent verdict → conservative <c>ping</c>.</summary>
    private static BundleAttention MapAttention(WireAttention? a)
    {
        if (a is null)
            return new BundleAttention("ping", 0.0, "no attention signal");
        var verdict = a.Verdict?.ToLowerInvariant() switch
        {
            "discard" => "discard",
            "promote" => "promote",
            "ping" => "ping",
            _ => "ping", // never auto-promote on an unrecognised verdict
        };
        return new BundleAttention(verdict, System.Math.Clamp(a.Score, 0.0, 1.0), a.Reason ?? "");
    }

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? items) =>
        items is null ? Array.Empty<string>()
        : items.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

    // ── wire DTOs ────────────────────────────────────────────────────────────────
    private sealed record WireRequest(
        [property: JsonPropertyName("recording_id")] string RecordingId,
        [property: JsonPropertyName("base_text")] string BaseText,
        [property: JsonPropertyName("language")] string Language);

    private sealed record WireResponse(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("entities")] List<string>? Entities,
        [property: JsonPropertyName("dates")] List<string>? Dates,
        [property: JsonPropertyName("links")] List<WireLink>? Links,
        [property: JsonPropertyName("timeline")] List<WireTimeline>? Timeline,
        [property: JsonPropertyName("attention")] WireAttention? Attention,
        [property: JsonPropertyName("participants")] List<WireParticipant>? Participants,
        [property: JsonPropertyName("garbled_spans")] List<WireSpan>? GarbledSpans);

    private sealed record WireLink(
        [property: JsonPropertyName("target")] string? Target,
        [property: JsonPropertyName("confidence")] double Confidence);

    private sealed record WireTimeline(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("fact")] string? Fact,
        [property: JsonPropertyName("source")] string? Source);

    private sealed record WireAttention(
        [property: JsonPropertyName("verdict")] string? Verdict,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record WireParticipant(
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("canonical_name")] string? CanonicalName,
        [property: JsonPropertyName("aliases")] List<string>? Aliases);

    private sealed record WireSpan(
        [property: JsonPropertyName("start")] int Start,
        [property: JsonPropertyName("end")] int End);
}

/// <summary>A recording-fact derivation failure carrying whether it is retryable (maps onto SCHEMAS §5).</summary>
public sealed class RecordingFactSourceException(string reason, bool retryable, Exception? inner = null)
    : Exception(reason, inner)
{
    public string Reason { get; } = reason;
    public bool Retryable { get; } = retryable;
}
