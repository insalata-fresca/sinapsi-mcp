namespace ApprovalBridge.Executor.Garmin;

/// <summary>
/// The SECRET result of an OAuth code→token exchange. It NEVER leaves the executor: it is stored server-side
/// by an <see cref="IGarminTokenStore"/> and only its non-secret <see cref="ExpiresAt"/> metadata appears in
/// the returned <c>result_schema</c> payload. The access/refresh tokens are the exact thing the seal protects.
/// </summary>
/// <param name="AccessToken">The access token — secret; server-side only.</param>
/// <param name="RefreshToken">The refresh token — secret; server-side only.</param>
/// <param name="ExpiresAt">When the access token expires — non-secret metadata.</param>
public sealed record GarminToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// The Garmin OAuth token endpoint, abstracted so tests inject a MOCK — E1.4 makes no real Garmin call and
/// touches no live network. A production implementation POSTs <c>grant_type=authorization_code</c> with the
/// client secret to Garmin's token URL; that implementation is out of scope for this shadow slice.
/// </summary>
public interface IGarminTokenEndpoint
{
    /// <summary>Exchange <paramref name="authCode"/> (agent-supplied, non-secret) + <paramref name="clientSecret"/>
    /// (read target-side via Path D) for a token. Throws <see cref="Sdk.ExecutorException"/> on a benign refusal.</summary>
    Task<GarminToken> ExchangeAsync(string authCode, string clientSecret, CancellationToken ct = default);
}

/// <summary>Persists the exchanged token SERVER-SIDE (Infisical / <c>0600</c> file) so it never returns to the
/// agent. The executor calls this after a successful exchange; the result payload confirms <c>stored:true</c>
/// without ever carrying the token.</summary>
public interface IGarminTokenStore
{
    /// <summary>Store <paramref name="token"/> server-side under the target identity. Returns nothing secret.</summary>
    Task StoreAsync(GarminToken token, CancellationToken ct = default);
}
