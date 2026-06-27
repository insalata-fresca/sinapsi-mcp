namespace Sinapsi.Forge.Tools;

/// <summary>
/// Surfaces real forge HTTP errors to the MCP client.
///
/// The MCP SDK (ModelContextProtocol.AspNetCore 1.3.0) swallows a thrown
/// <see cref="ForgeApiException"/> into a generic "An error occurred invoking 'X'"
/// — dropping the real HTTP status + body the adapter captured. Wrapping a tool
/// body in <see cref="RunAsync"/> turns that exception into a structured success
/// payload the client can read: <c>{ ok = false, status, error }</c>. A successful
/// call returns its normal payload unchanged.
///
/// This is the by-design surface for release endpoints that 404 when a repo's
/// Releases unit is disabled (<c>has_releases:false</c>) — the status + body make
/// the cause legible instead of a generic invoke error.
/// </summary>
public static class ForgeToolGuard
{
    /// <summary>Run a tool body, mapping a forge/operational failure to a structured error
    /// object instead of letting the MCP SDK swallow it into the generic invoke message.</summary>
    public static async Task<object> RunAsync(Func<Task<object>> body)
    {
        try
        {
            return await body();
        }
        catch (ForgeApiException ex)
        {
            // Real upstream HTTP failure — status + body (e.g. 404 when has_releases:false).
            return new { ok = false, status = ex.Status, error = ex.Message };
        }
        catch (Exception ex) when (
            ex is ArgumentException        // e.g. not exactly one upload source
              or IOException               // incl. FileNotFoundException (bad source_path)
              or HttpRequestException      // source_url fetch failed
              or FormatException           // bad content_base64
              or InvalidOperationException)
        {
            // Operational failures the operator must see verbatim, not as "An error occurred".
            return new { ok = false, status = (int?)null, error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }
}
