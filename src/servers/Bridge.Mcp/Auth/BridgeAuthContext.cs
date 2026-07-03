namespace Bridge.Mcp.Auth;

/// <summary>
/// Scopes implicitly granted to legacy bearer holders.
/// The legacy token is the pre-OAuth admin token (Phase 1-3); it grants the
/// full surface EXCEPT bridge:read:facts_sensitive which requires explicit Phase 5 OAuth consent.
/// Matches the Python LEGACY_SCOPES frozenset exactly.
/// </summary>
public static class LegacyScopes
{
    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        "bridge:deposit",
        "bridge:read:documents",
        "bridge:read:facts",
        "bridge:read:emails",
        "bridge:context_pack",
    };
}

/// <summary>
/// Result of a successful authentication: which path matched (bearer|jwt),
/// the stable subject (bearer hash prefix / JWT sub), and the granted scope set.
/// Mirrors the Python AuthContext dataclass.
/// </summary>
public sealed class BridgeAuthContext
{
    /// <summary>"bearer" or "jwt"</summary>
    public required string Mode { get; init; }

    /// <summary>
    /// For bearer: "legacy-bearer". For JWT: the sub claim.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>Granted scopes (may be a superset of the JWT scope claim for trusted issuers).</summary>
    public required IReadOnlySet<string> Scopes { get; init; }

    /// <summary>Raw token value used as the rate-limit key (sha256 key prefix).</summary>
    public required string RawToken { get; init; }

    public bool HasScope(string scope) => Scopes.Contains(scope);
}
