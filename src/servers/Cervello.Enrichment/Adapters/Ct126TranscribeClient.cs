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
/// (multipart: <c>file</c> + <c>language</c> + <c>response_format=json</c>) and maps the returned
/// <c>{ text }</c> onto the immutable <see cref="BaseTranscript"/> substrate (spec
/// <c>text-correction</c> → "Base transcript is the correction substrate"). Bearer-gated via
/// <see cref="IBearerProvider"/> (agent-free); audio is a transient request payload only.
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

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IBearerProvider _bearer;
    private readonly ILogger _log;

    public Ct126TranscribeClient(HttpClient http, IBearerProvider bearer, ILogger<Ct126TranscribeClient>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _bearer = bearer ?? throw new ArgumentNullException(nameof(bearer));
        _log = log ?? NullLogger<Ct126TranscribeClient>.Instance;
    }

    public async Task<BaseTranscript> TranscribeAsync(
        ReadOnlyMemory<byte> audio, string format, string language, CancellationToken ct = default)
    {
        if (audio.IsEmpty) throw new ArgumentException("audio must be non-empty", nameof(audio));
        if (string.IsNullOrWhiteSpace(format)) throw new ArgumentException("format must be non-empty", nameof(format));
        if (string.IsNullOrWhiteSpace(language)) throw new ArgumentException("language must be non-empty", nameof(language));

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audio.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", $"recording.{format}");
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
                return new BaseTranscript(wire.Text, language);
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
