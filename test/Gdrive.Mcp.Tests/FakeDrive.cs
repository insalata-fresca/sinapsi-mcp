using System.Net;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;

namespace Gdrive.Mcp.Tests;

// ---------------------------------------------------------------------------
// Test doubles for a DriveService whose transport is fully controlled, so the
// hardening paths can be proven WITHOUT a live Google account:
//
//   * ThrowingDrive()  — every HTTP call throws. Used by the tool-guard tests to
//     prove a bad parameter short-circuits to a structured error BEFORE any HTTP
//     round-trip is attempted (if the guard were missing, the throw would surface
//     as an unhandled exception / wrong shape instead).
//
//   * RespondingDrive(status, body) — every HTTP call returns a canned response.
//     Used to prove that an upstream error body (which can carry a secret) is
//     routed through GdriveErrors.Sanitize before it reaches the caller.
//
// The injection point is the Google.Apis IHttpClientFactory: we subclass the
// stock HttpClientFactory and override CreateHandler to return a
// ConfigurableMessageHandler over our own inner HttpMessageHandler.
// ---------------------------------------------------------------------------

/// <summary>An inner handler whose behaviour is a single injected delegate.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(_respond(request));
}

/// <summary>A Google.Apis client factory that swaps in a controlled inner handler.</summary>
internal sealed class StubClientFactory : HttpClientFactory
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public StubClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args)
        => new ConfigurableMessageHandler(new StubHandler(_respond));
}

internal static class FakeDrive
{
    /// <summary>A DriveService whose every HTTP call throws — proves a validation
    /// guard short-circuited before any transport was touched.</summary>
    internal static DriveService Throwing() =>
        Build(_ => throw new InvalidOperationException("HTTP transport must not be reached"));

    /// <summary>A DriveService whose every HTTP call returns the given status +
    /// body — lets a test inject an upstream error body (incl. a fake secret).</summary>
    internal static DriveService Responding(HttpStatusCode status, string body) =>
        Build(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        });

    /// <summary>A DriveService whose every HTTP call is recorded into
    /// <paramref name="captured"/> (method + full request body bytes) before
    /// responding with the given canned success body. Lets a test prove a
    /// binary upload reaches the transport byte-exact (e.g. the request body
    /// contains the decoded bytes verbatim, not a mangled/re-encoded copy).
    ///
    /// The official client's <c>Files.Create(meta, stream, mimeType).UploadAsync()</c>
    /// uses the Drive RESUMABLE upload protocol: (1) POST
    /// <c>uploadType=resumable</c> to open a session — the client requires a
    /// <c>Location</c> response header naming the session URI, then (2) PUT the
    /// raw bytes to that session URI. This fake plays both legs: step 1 gets a
    /// synthesized same-fake session URI back in <c>Location</c>; step 2 (the
    /// PUT carrying the actual decoded bytes) gets the canned success body.</summary>
    internal static DriveService Capturing(List<CapturedRequest> captured, string responseBody) =>
        Build(req =>
        {
            var bodyBytes = req.Content is not null
                ? req.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                : Array.Empty<byte>();
            captured.Add(new CapturedRequest(req.Method.Method, req.RequestUri?.ToString() ?? "", bodyBytes));

            var isResumableInitiate =
                req.Method == HttpMethod.Post &&
                (req.RequestUri?.Query.Contains("uploadType=resumable") ?? false);

            if (isResumableInitiate)
            {
                var sessionUri = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=fake-session";
                var initiateResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
                initiateResponse.Headers.Add("Location", sessionUri);
                return initiateResponse;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        });

    private static DriveService Build(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new BaseClientService.Initializer
        {
            HttpClientFactory = new StubClientFactory(respond),
            ApplicationName = "gdrive-mcp-tests",
        });
}

/// <summary>One HTTP request captured by <see cref="FakeDrive.Capturing"/>.</summary>
internal sealed record CapturedRequest(string Method, string Uri, byte[] Body);
