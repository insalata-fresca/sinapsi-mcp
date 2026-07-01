using Zitadel.Mcp.Api;

namespace Zitadel.Mcp.Tools;

/// <summary>
/// Surfaces real ZITADEL HTTP errors to the MCP client.
///
/// The MCP SDK swallows a thrown <see cref="ZitadelApiException"/> into a generic
/// "An error occurred invoking 'X'" — dropping the real HTTP status + body. Wrapping a
/// tool body in <see cref="RunAsync"/> turns that exception into a structured success
/// payload the client can read: <c>{ ok = false, status, error }</c>. A successful call
/// returns its normal payload unchanged.
///
/// <para>
/// Every surfaced error string is routed through <see cref="ZitadelErrors.Sanitize"/> so a
/// credential that leaked into the upstream body (or into a transport exception message)
/// is redacted and length-capped before it reaches a caller — uniformly across all tools,
/// reads included.
/// </para>
/// </summary>
public static class ZitadelToolGuard
{
    public static async Task<object> RunAsync(Func<Task<object>> body)
    {
        try
        {
            return await body();
        }
        catch (ZitadelApiException ex)
        {
            return new { ok = false, status = ex.Status, error = ZitadelErrors.Sanitize(ex.Message) };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return new { ok = false, status = (int?)null, error = ZitadelErrors.Sanitize($"{ex.GetType().Name}: {ex.Message}") };
        }
    }
}
