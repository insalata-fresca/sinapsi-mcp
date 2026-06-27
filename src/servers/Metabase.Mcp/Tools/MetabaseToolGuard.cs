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
/// </summary>
public static class MetabaseToolGuard
{
    public static async Task<object> RunAsync(Func<Task<object>> body)
    {
        try
        {
            return await body();
        }
        catch (MetabaseApiException ex)
        {
            return new { ok = false, status = ex.Status, error = ex.Message };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return new { ok = false, status = (int?)null, error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }
}
