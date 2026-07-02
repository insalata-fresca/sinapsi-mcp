using System.Text.Json;
using Metabase.Mcp.Api;

namespace Metabase.Mcp.Tools;

/// <summary>
/// Surfaces real Metabase HTTP errors to the MCP client.
///
/// The MCP SDK swallows a thrown <see cref="MetabaseApiException"/> into a generic
/// "An error occurred invoking 'X'" — dropping the real HTTP status + body. Wrapping a
/// tool body in <see cref="RunAsync"/> turns that exception into a structured success
/// payload the client can read: <c>{ ok = false, status, error }</c>. A successful call
/// returns its normal payload unchanged.
///
/// <para>
/// Every surfaced error string is routed through <see cref="MetabaseErrors.Sanitize"/> so a
/// credential that leaked into the upstream body (or into a transport exception message) is
/// redacted and length-capped before it reaches a caller — uniformly across all tools, reads
/// included. A malformed JSON body/patch surfaces as a clean <see cref="JsonException"/>-derived
/// error rather than an unhandled SDK failure.
/// </para>
/// </summary>
public static class MetabaseToolGuard
{
    /// <summary>Return the structured <c>{ ok:false, error }</c> rejection envelope produced by
    /// a validation guard, with the reason sanitized and length-capped for consistency with the
    /// upstream-error envelope. Validation reasons never carry secrets, but routing them through
    /// the same sanitizer keeps a single, uniform error contract.</summary>
    public static object Rejected(string reason)
        => new { ok = false, status = (int?)null, error = MetabaseErrors.Sanitize(reason) };

    public static async Task<object> RunAsync(Func<Task<object>> body)
    {
        try
        {
            return await body();
        }
        catch (MetabaseApiException ex)
        {
            return new { ok = false, status = ex.Status, error = MetabaseErrors.Sanitize(ex.Message) };
        }
        catch (JsonException ex)
        {
            // A malformed JSON body/patch that slipped past validation (or an upstream
            // non-JSON payload where JSON was parsed) surfaces as a clean structured error.
            return new { ok = false, status = (int?)null, error = MetabaseErrors.Sanitize($"invalid JSON: {ex.Message}") };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return new { ok = false, status = (int?)null, error = MetabaseErrors.Sanitize($"{ex.GetType().Name}: {ex.Message}") };
        }
    }
}
