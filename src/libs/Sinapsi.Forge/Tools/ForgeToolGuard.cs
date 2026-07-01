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
///
/// <para>
/// Every error string this guard surfaces — the upstream HTTP body carried on a
/// <see cref="ForgeApiException"/> and any operational exception message — is passed
/// through <see cref="SinapsiForgeErrors.Sanitize"/> first, so a token/secret/PEM key
/// that reached a forge response body or an exception can never reach a caller. The
/// numeric HTTP <c>status</c> is taken verbatim from the exception (a RAW verdict
/// computed before sanitizing), so a redaction can never change what status a caller
/// is told.
/// </para>
/// </summary>
public static class ForgeToolGuard
{
    /// <summary>Run a tool body, mapping a forge/operational failure to a structured error
    /// object instead of letting the MCP SDK swallow it into the generic invoke message.
    /// Every surfaced error string is scrubbed of credential/key material.</summary>
    public static async Task<object> RunAsync(Func<Task<object>> body)
    {
        try
        {
            return await body();
        }
        catch (ForgeApiException ex)
        {
            // Real upstream HTTP failure — status is the RAW verdict; the body is scrubbed.
            return new { ok = false, status = ex.Status, error = SinapsiForgeErrors.Sanitize(ex.Message) };
        }
        catch (Exception ex) when (
            ex is ArgumentException        // e.g. not exactly one upload source
              or IOException               // incl. FileNotFoundException (bad source_path)
              or HttpRequestException      // source_url fetch failed
              or FormatException           // bad content_base64
              or InvalidOperationException // e.g. unsupported capability
              or TimeoutException          // HttpClient.Timeout inner exception
              or TaskCanceledException)    // HttpClient.Timeout surfaces as a cancelled task
        {
            // Operational failures the operator must see, scrubbed, not as "An error occurred".
            // A hung upstream cut off by HttpClient.Timeout lands here as a structured error
            // rather than an unhandled throw the SDK would flatten to a generic invoke message.
            return new { ok = false, status = (int?)null, error = SinapsiForgeErrors.Sanitize($"{ex.GetType().Name}: {ex.Message}") };
        }
    }

    /// <summary>
    /// Validation-first overload: run <paramref name="validate"/> at the TOP of the
    /// tool BEFORE any HTTP call. If it returns a non-null reason, short-circuit with
    /// <c>{ ok:false, status:null, error }</c> and never invoke <paramref name="body"/>;
    /// otherwise run the body under the same error-scrubbing guard. This is the
    /// canonical shape for a hardened tool: param validation → HTTP → sanitized error.
    /// </summary>
    public static Task<object> RunAsync(Func<string?> validate, Func<Task<object>> body)
    {
        var reason = validate();
        if (reason is not null)
            return Task.FromResult<object>(new { ok = false, status = (int?)null, error = reason });
        return RunAsync(body);
    }
}
