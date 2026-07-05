// ---------------------------------------------------------------------------
// SearchAuth - the GET /search Bearer-token gate (M5-secure fix).
//
// Root cause fixed here: INDEXER_SEARCH_TOKEN was read NOWHERE in the service
// (only referenced in a Program.cs comment). The /search route was gated
// SOLELY by whether it was mounted at all (INDEXER_CAP_SEARCH_HTTP) — once
// mounted, ANY caller got 200 + full result content, token or not. This type
// is the missing enforcement: when a token is configured, every request MUST
// carry a matching "Authorization: Bearer <token>" header.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Sinapsi.Indexer;

/// <summary>
/// Pure, unit-testable Bearer-token check for <c>GET /search</c>. Mirrors the
/// legacy-bearer half of <c>Bridge.Mcp.Auth.BridgeAuthMiddleware</c>
/// (constant-time compare via <see cref="CryptographicOperations.FixedTimeEquals"/>),
/// scoped down to the indexer's single static token (no JWT here).
/// </summary>
internal static class SearchAuth
{
    /// <summary>
    /// Returns <c>true</c> iff the request is authorized to hit <c>/search</c>.
    ///
    /// <list type="bullet">
    /// <item><description><paramref name="configuredToken"/> is <c>null</c> (i.e.
    /// <c>INDEXER_SEARCH_TOKEN</c> unset) — today's documented "route disabled for
    /// this tenant" case is handled by not mounting the route at all
    /// (<c>caps.SearchHttp</c> in Program.cs); if this method is ever reached with
    /// no configured token, fail CLOSED (return false) rather than silently
    /// allowing every caller through.</description></item>
    /// <item><description>Otherwise: the request's <c>Authorization</c> header must be
    /// exactly <c>Bearer &lt;configuredToken&gt;</c>, compared in constant time.</description></item>
    /// </list>
    /// </summary>
    internal static bool IsAuthorized(string? authorizationHeader, string? configuredToken)
    {
        if (string.IsNullOrEmpty(configuredToken))
            return false; // fail closed: no token configured => nothing is authorized here.

        var presented = ExtractBearer(authorizationHeader);
        if (presented is null)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(configuredToken));
    }

    /// <summary>Extract the token from an <c>Authorization: Bearer &lt;token&gt;</c>
    /// header. Returns <c>null</c> when the header is missing, empty, or not a
    /// well-formed "Bearer &lt;token&gt;" value.</summary>
    internal static string? ExtractBearer(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var parts = header.Trim().Split(null, 2);
        if (parts.Length != 2 || !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = parts[1].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>Write the standard 401 body for an unauthorized <c>/search</c> call.</summary>
    internal static Task WriteUnauthorizedAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Json(new { error = "unauthorized" }, statusCode: 401).ExecuteAsync(ctx);
    }
}
