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

    private static DriveService Build(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new BaseClientService.Initializer
        {
            HttpClientFactory = new StubClientFactory(respond),
            ApplicationName = "gdrive-mcp-tests",
        });
}
