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

    /// <summary>
    /// Cervello Surface A scopes (ACCESS.md §2). These are auto-granted ONLY when the
    /// deployment exposes cervello (CERVELLO_EXPOSED=true) — the same env flag that gates
    /// cervello exposure everywhere else. Zitadel's auth-code flow strips these unknown
    /// custom scopes from the token, so a cervello-exposed deployment auto-grants them here
    /// so the scope-claim check passes; a non-cervello deployment never grants them.
    ///
    /// This does NOT weaken cervello access control: the workspace-binding gate (ACCESS §2.2)
    /// and the deposit path-guard (ACCESS §4) are enforced independently server-side on
    /// CT146-cervello. The bridge only forwards the bearer; this grant makes the SCOPE claim
    /// pass, nothing more.
    /// </summary>
    public static readonly HashSet<string> Cervello = new(StringComparer.Ordinal)
    {
        "bridge:cervello:read",
        "bridge:cervello:deposit",
    };

    /// <summary>
    /// Personal-health scope (health-mcp + withings-mcp via the CT121 PEP). Auto-granted ONLY when
    /// the deployment exposes health (HEALTH_EXPOSED=true) — the same rationale as <see cref="Cervello"/>:
    /// Zitadel's auth-code flow strips unknown custom scopes from the token, so a health-exposed
    /// deployment auto-grants the scope here to make the scope-claim check pass. The real access
    /// control is the agentgateway PEP grant on the bridge's scoped agent identity — the bridge only
    /// forwards a minted agent JWT; this grant makes the SCOPE claim pass, nothing more.
    /// </summary>
    public static readonly HashSet<string> Health = new(StringComparer.Ordinal)
    {
        "bridge:health:read",
    };

    /// <summary>
    /// The effective auto-granted scope set for this deployment: always <see cref="All"/>, plus
    /// <see cref="Cervello"/> when <paramref name="cervelloExposed"/> is true, plus <see cref="Health"/>
    /// when <paramref name="healthExposed"/> is true. Used by both auth paths (legacy bearer + Zitadel
    /// JWT) so an exposed session authorizes the tools without those scopes surviving in the token claim.
    /// </summary>
    public static IReadOnlyCollection<string> Granted(bool cervelloExposed, bool healthExposed = false)
    {
        if (!cervelloExposed && !healthExposed) return All;
        var set = new HashSet<string>(All, StringComparer.Ordinal);
        if (cervelloExposed) foreach (var s in Cervello) set.Add(s);
        if (healthExposed) foreach (var s in Health) set.Add(s);
        return set;
    }
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
