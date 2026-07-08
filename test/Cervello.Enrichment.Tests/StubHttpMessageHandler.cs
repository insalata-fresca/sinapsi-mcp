using System.Net;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// A scriptable <see cref="HttpMessageHandler"/> for the live HTTP adapters' unit tests. It records
/// every outgoing <see cref="HttpRequestMessage"/> (so a test can assert the request SHAPE — path,
/// bearer header, body) and returns a scripted <see cref="HttpResponseMessage"/> (so a test can
/// assert the RESPONSE MAPPING + error→retryable classification). NO real network, NO live endpoint.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;

    /// <summary>The requests seen (in order), with their captured bodies.</summary>
    public List<CapturedRequest> Requests { get; } = [];

    private StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

    /// <summary>Respond with a fixed status + JSON body for every request.</summary>
    public static StubHttpMessageHandler Json(HttpStatusCode status, string jsonBody) =>
        new((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
        });

    /// <summary>Respond with a fixed status + no/plain body (e.g. a 500 with a reason phrase).</summary>
    public static StubHttpMessageHandler Status(HttpStatusCode status, string body = "") =>
        new((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain"),
        });

    /// <summary>Throw a transport <see cref="HttpRequestException"/> (connection reset analogue).</summary>
    public static StubHttpMessageHandler Throwing(Exception ex) =>
        new((_, _) => throw ex);

    /// <summary>Custom responder with access to the request + its captured body text.</summary>
    public static StubHttpMessageHandler Custom(Func<HttpRequestMessage, string, HttpResponseMessage> respond) =>
        new(respond);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Content?.Headers.ContentType?.MediaType,
            body));
        return _respond(request, body);
    }
}

/// <summary>A captured outbound request: method, uri, bearer, content-type, and the body text.</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? AuthScheme,
    string? Bearer,
    string? ContentType,
    string Body);
