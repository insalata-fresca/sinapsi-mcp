namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// A named trust/security surface a change can touch (home-server <c>docs/65 §3.5</c> enumerates
/// these). Carried on the verdict + envelope as the EFFECT CLASSIFICATION the operator sees when a
/// change escalates (<c>docs/65 §3.5</c> required evidence: "which trust surface, which
/// relation/credential/config"). Detection is by PATH/CONTENT rules, never by an LLM.
/// </summary>
public enum TrustSurface
{
    /// <summary>OpenFGA relations/tuples/model — who-can-call-what (<c>policies/openfga/**</c>).</summary>
    OpenFgaRelation,

    /// <summary>A credential/secret/key/cert/token/nkey-seed surface (rotation, exposure, or
    /// embedded material).</summary>
    Credential,

    /// <summary>Protected infra: the gateway PEP, sinapsi-mcp-authz, harness gate config,
    /// self-hosted CI runners, webhooks, step-ca provisioners.</summary>
    ProtectedInfra,

    /// <summary>nats accounts / auth-callout / auth config — the security bus trust plane.</summary>
    NatsAuthConfig,

    /// <summary>The Q2 capability model / read-file policy that decides read vs write vs
    /// secret-read (<c>CommandCapabilities.cs</c>, <c>ReadFilePolicy.cs</c>, exec-allow-readonly).</summary>
    CapabilityModel,

    /// <summary>An enforced governance authority doc whose words are consumed as policy
    /// (root <c>CLAUDE.md</c>, <c>services/claude-root/rules/CLAUDE.md</c>, the autonomy charter).</summary>
    GovernanceAuthorityDoc,

    /// <summary>A new/widened outbound egress or external call.</summary>
    Egress,

    /// <summary>An identity/credential creation → a NEW trust boundary
    /// (<c>docs/60 §8.1</c>, <c>docs/62 §2.4</c>).</summary>
    NewTrustBoundary,

    /// <summary>Flipping a live authorization layer shadow→enforce (SHADOW/ASK_GATE_MODE/
    /// DENY_FLOOR_MODE) — a hard-floor DENY surface (<c>docs/62 §2.2</c>).</summary>
    EnforcementFlip,

    /// <summary>A credential force/overwrite (<c>--force</c>/<c>--overwrite</c>/<c>-f</c> on a
    /// key/cert/token/password path) — a hard-floor DENY surface (<c>docs/62 §2.3</c>).</summary>
    CredentialForceOverwrite,

    /// <summary>Disarming a security control: a removed authorization check, a removed audit/
    /// telemetry emission on a security path, a bypass of the trust plane — hard-floor DENY.</summary>
    SecurityControlDisarm,

    /// <summary>A hard-coded credential/token literal in source — hard-floor DENY.</summary>
    HardcodedCredential,

    /// <summary>Softening/disarming the catastrophic DENY floor / a hard-safety rule — hard-floor
    /// DENY (<c>docs/61 §7.4</c>).</summary>
    FloorWeakening,

    /// <summary><c>wg0</c> on Genova — sacrosanct, always escalated, hard-floor DENY
    /// (<c>CLAUDE.md</c> rule 1).</summary>
    Wg0Genova,

    /// <summary>An op that can only be applied via Tier-3 god-mode (<c>pct exec</c>/<c>pct set</c>,
    /// snapshot/rollback) — the mechanism is the elevation; hard-floor DENY for the auto-pipeline
    /// (<c>docs/62 §2.1</c>).</summary>
    GodModeRequired,
}
