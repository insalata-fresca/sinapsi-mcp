using System.Net.Http.Headers;

namespace Zitadel.Mcp.Auth;

/// <summary>
/// SA-key-mode auth for the typed <see cref="Api.ZitadelClient"/> HttpClient. On every request it
/// mints-or-reuses a short-lived JWT bearer via <see cref="JwtBearerTokenProvider"/> and attaches
/// it as <c>Authorization: Bearer</c>, plus the <c>Host</c> + <c>X-Forwarded-Proto: https</c>
/// headers ZITADEL needs when the API root is a LAN-bypass origin. This is only registered in
/// SA-key mode; in PAT mode the static bearer set on the client's default headers is used instead
/// (this handler is not in the pipeline).
/// </summary>
public sealed class SaKeyAuthHandler(JwtBearerTokenProvider tokens, ZitadelConfig cfg) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Override the wire-level Host so ZITADEL validates the issuer against the public host even
        // when the request authority is a LAN-bypass IP. X-Forwarded-Proto: https mirrors what the
        // TLS-terminating proxy would set, so ZITADEL's issuer check passes over plain internal HTTP.
        request.Headers.Host = cfg.HostHeader;
        if (!request.Headers.Contains("X-Forwarded-Proto"))
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
