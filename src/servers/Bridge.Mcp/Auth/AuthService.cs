namespace Bridge.Mcp.Auth;

/// <summary>
/// Per-request auth + rate-limit enforcement helper.
/// Tools call <see cref="Authorize"/> at their top to:
///   1. assert the current request has the required scope, and
///   2. check the sliding-window rate limit for the appropriate bucket.
/// Mirrors the Python _authorize(scope, bucket, limit_per_min) function.
///
/// Throws <see cref="BridgeToolException"/> on auth or rate-limit failure
/// so the tool can catch it and return a structured error response.
/// </summary>
public sealed class AuthService(BridgeConfig config, BridgeRateLimiter limiter)
{
    // ── Scope constants (mirrors Python globals) ───────────────────────────────
    public const string DepositScope              = "bridge:deposit";
    public const string ReadDocumentsScope        = "bridge:read:documents";
    public const string ReadFactsScope            = "bridge:read:facts";
    public const string ReadFactsSensitiveScope   = "bridge:read:facts_sensitive";
    public const string ContextPackScope          = "bridge:context_pack";
    // Cervello Surface A (S50 L3): a cervello-scoped credential distinct from the shared bridge
    // token (ACCESS.md §2). read = list the pending open-points; deposit = answer (write-back).
    public const string CervelloReadScope         = "bridge:cervello:read";
    public const string CervelloDepositScope      = "bridge:cervello:deposit";

    // ── Bucket names ──────────────────────────────────────────────────────────
    public const string DepositBucket   = "deposit";
    public const string ReadBucket      = "read";
    public const string SensitiveBucket = "sensitive";

    /// <summary>
    /// Verify the current request carries <paramref name="scope"/> and isn't over
    /// its rate limit. Returns the validated <see cref="BridgeAuthContext"/>.
    /// Throws <see cref="BridgeToolException"/> on failure.
    /// </summary>
    public BridgeAuthContext Authorize(string scope, string bucket, int limitPerMin)
    {
        var ctx = BridgeAuthState.CurrentAuth;
        if (ctx is null)
            throw new BridgeToolException("missing_bearer", "No bearer token provided");

        if (!ctx.HasScope(scope))
        {
            var resourceMd = $"{config.BridgeBaseUrl.TrimEnd('/')}/.well-known/oauth-protected-resource";
            throw new BridgeToolException(
                "insufficient_scope",
                $"Scope {scope} required. resource_metadata=\"{resourceMd}\"");
        }

        var decision = limiter.Check(ctx.RawToken, bucket, limitPerMin);
        if (!decision.Allowed)
            throw new BridgeToolException(
                "rate_limited",
                $"{bucket} bucket exceeded; retry after {decision.RetryAfterSeconds:F1}s");

        return ctx;
    }
}

/// <summary>
/// Structured tool error — code + human message, mirroring the Python ToolError(code, message).
/// Tools catch this and return { error: code, message: ... } to the MCP caller.
/// </summary>
public sealed class BridgeToolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
