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
/// Live <see cref="ITranscribeClient"/> over CT126 speaches (<c>:8000</c>), an OpenAI-compatible
/// ASR server. Base transcription posts the recording audio to <c>POST /v1/audio/transcriptions</c>
/// (multipart: <c>file</c> + <c>model</c> + optional <c>language</c> + <c>response_format=json</c>)
/// and maps the returned <c>{ text }</c> onto the immutable <see cref="BaseTranscript"/> substrate
/// (spec <c>text-correction</c> → "Base transcript is the correction substrate"). Bearer-gated via
/// <see cref="IBearerProvider"/> (agent-free); audio is a transient request payload only.
///
/// <para><b><c>model</c> is REQUIRED by speaches</b> (OpenAI-compatible; omitting it is rejected with
/// HTTP 422 Unprocessable Entity). The model id is configuration, never hardcoded here — supplied via
/// the constructor and threaded from <see cref="EnrichmentConfig.TranscribeModel"/>
/// (<c>CERVELLO_TRANSCRIBE_MODEL</c>), defaulting to the model speaches has loaded on CT126
/// (<c>Systran/faster-whisper-large-v3</c>).</para>
///
/// <para><b><c>language</c> is OPTIONAL — auto-detect when unset.</b> The recording corpus is
/// multilingual (Italian-dominant + French/other); forcing a single configured language mis-transcribes
/// every recording not in that language. When the caller's <c>language</c> is null/empty/<c>"auto"</c>
/// the <c>language</c> form field is OMITTED entirely so speaches auto-detects per recording. A
/// concrete non-empty, non-<c>"auto"</c> value is still sent verbatim (e.g. for a caller that knows the
/// language out-of-band).</para>
///
/// <para><b>Failure classification</b> mirrors the diarize-embed contract so the pipeline maps it
/// onto <c>failed_retryable</c> vs <c>failed_terminal</c> uniformly: timeout / 5xx / transport →
/// <see cref="TranscribeTransientException"/>; 4xx (undecodable audio / bad request) →
/// <see cref="TranscribeTerminalException"/>. The adapter never fabricates a transcript on failure.</para>
/// </summary>
public sealed class Ct126TranscribeClient : ITranscribeClient
{
    /// <summary>The speaches OpenAI-compatible transcription route.</summary>
    public const string RoutePath = "/v1/audio/transcriptions";

    /// <summary>The logical bearer audience for CT126 egress.</summary>
    public const string Audience = "ct126-speaches";

    /// <summary>
    /// The configured language sentinel meaning "omit the <c>language</c> field / auto-detect". Also
    /// treated as auto-detect: null or empty/whitespace.
    /// </summary>
    public const string AutoLanguage = "auto";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IBearerProvider _bearer;
    private readonly string _model;
    private readonly ILogger _log;

    public Ct126TranscribeClient(
        HttpClient http,
        IBearerProvider bearer,
        EnrichmentConfig cfg,
        ILogger<Ct126TranscribeClient>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _bearer = bearer ?? throw new ArgumentNullException(nameof(bearer));
        ArgumentNullException.ThrowIfNull(cfg);
        if (string.IsNullOrWhiteSpace(cfg.TranscribeModel))
            throw new ArgumentException("EnrichmentConfig.TranscribeModel must be non-empty", nameof(cfg));
        _model = cfg.TranscribeModel;
        _log = log ?? NullLogger<Ct126TranscribeClient>.Instance;
    }

    public async Task<BaseTranscript> TranscribeAsync(
        ReadOnlyMemory<byte> audio, string format, string language, CancellationToken ct = default)
    {
        if (audio.IsEmpty) throw new ArgumentException("audio must be non-empty", nameof(audio));
        if (string.IsNullOrWhiteSpace(format)) throw new ArgumentException("format must be non-empty", nameof(format));

        // Auto-detect: null/empty/"auto" → omit the language field entirely so speaches detects it
        // per recording (the corpus is multilingual — forcing one language mis-transcribes the rest).
        var isAuto = string.IsNullOrWhiteSpace(language) ||
                     string.Equals(language, AutoLanguage, StringComparison.OrdinalIgnoreCase);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audio.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", $"recording.{format}");
        content.Add(new StringContent(_model), "model");
        if (!isAuto)
            content.Add(new StringContent(language), "language");
        content.Add(new StringContent("json"), "response_format");

        using var req = new HttpRequestMessage(HttpMethod.Post, RoutePath) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _bearer.GetBearerAsync(Audience, ct).ConfigureAwait(false));

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new TranscribeTransientException("CT126 transcribe timed out", e);
        }
        catch (HttpRequestException e)
        {
            throw new TranscribeTransientException($"CT126 transcribe transport error: {e.Message}", e);
        }

        using (res)
        {
            if (res.IsSuccessStatusCode)
            {
                var wire = await res.Content.ReadFromJsonAsync<WireTranscription>(_json, ct).ConfigureAwait(false);
                if (wire?.Text is null)
                    throw new TranscribeTerminalException("CT126 transcribe 200 with no 'text'");
                // response_format=json returns only {text} — speaches does not echo the detected
                // language back on this route, so an auto-detected transcript is labelled with the
                // AutoLanguage sentinel (honest: "auto-detected, label unknown") rather than the
                // caller's un-sent language value.
                return new BaseTranscript(wire.Text, isAuto ? AutoLanguage : language);
            }

            var reason = res.ReasonPhrase ?? "no detail";
            var code = (int)res.StatusCode;
            if (res.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout)
                throw new TranscribeTransientException($"CT126 transcribe {code}: {reason}");
            throw new TranscribeTerminalException($"CT126 transcribe {code}: {reason}");
        }
    }

    private sealed record WireTranscription([property: JsonPropertyName("text")] string? Text);
}

/// <summary>Base for CT126 transcribe failures (retryable flag maps onto SCHEMAS §5 states).</summary>
public abstract class TranscribeException : Exception
{
    protected TranscribeException(string reason, bool retryable, Exception? inner = null) : base(reason, inner)
    {
        Reason = reason;
        Retryable = retryable;
    }

    public string Reason { get; }
    public bool Retryable { get; }
}

/// <summary>Transient CT126 error (timeout / 5xx / transport) → <c>failed_retryable</c>.</summary>
public sealed class TranscribeTransientException(string reason, Exception? inner = null)
    : TranscribeException(reason, retryable: true, inner);

/// <summary>Terminal CT126 error (4xx / undecodable audio) → <c>failed_terminal</c>.</summary>
public sealed class TranscribeTerminalException(string reason, Exception? inner = null)
    : TranscribeException(reason, retryable: false, inner);
