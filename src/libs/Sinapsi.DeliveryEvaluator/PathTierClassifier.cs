using System.Text.RegularExpressions;

namespace Sinapsi.DeliveryEvaluator;

/// <summary>The result of classifying a single path: the tier it maps to (null when the path is
/// empty/unknowable, a fail-safe uncertainty) and any surface signals it raises.</summary>
public sealed record PathClassification(RiskTier? Tier, IReadOnlyList<RiskSignal> Signals);

/// <summary>
/// Step 1 of the rubric's determinism-first classification (home-server <c>docs/65 §4</c>): map a
/// touched path to its most-severe candidate tier via a static PATH table — before any LLM sees the
/// change. Ordered most-severe first so trust-plane paths win. Case-insensitive; the untrusted PR
/// title/body is never an input here.
/// </summary>
public static class PathTierClassifier
{
    // Trust-plane path signatures (docs/65 §3.5). Each maps a path to a trust surface.
    private static readonly (Regex Rx, TrustSurface Surface, string Code, string Desc)[] TrustPlanePaths =
    {
        (Re(@"(^|/)policies/openfga/|openfga"), TrustSurface.OpenFgaRelation, "path-openfga",
            "touches OpenFGA relations/tuples/model (who-can-call-what)"),
        (Re(@"\.(pem|key|crt|cert|p12|pfx)$|(^|/)secrets?(/|\.)|(^|/)credentials?(/|\.)|nkey|id_rsa|(^|/)[^/]*password[^/]*"),
            TrustSurface.Credential, "path-credential", "touches credential/secret/key/cert material"),
        (Re(@"agentgateway|mcpauthorization|sinapsi-mcp-authz|(^|/)pep(/|\.|$)"),
            TrustSurface.ProtectedInfra, "path-gateway-pep", "touches the gateway PEP / authz protected infra"),
        (Re(@"nats.*(auth|account|callout)|auth-?callout|(^|/)accounts?\.conf"),
            TrustSurface.NatsAuthConfig, "path-nats-auth", "touches nats auth/accounts/auth-callout config"),
        (Re(@"claude-root/hooks|ask-?gate|deny-?floor|exec-allow-readonly"),
            TrustSurface.ProtectedInfra, "path-harness-gate", "touches the harness ask-gate / deny-floor config"),
        (Re(@"CommandCapabilities\.cs|ReadFilePolicy\.cs|CommandAuthorizer"),
            TrustSurface.CapabilityModel, "path-capability-model", "touches the Q2 capability / read-file policy model"),
        (Re(@"step-?ca.*provisioner|provisioner.*step-?ca|(^|/)webhooks?(/|\.)"),
            TrustSurface.ProtectedInfra, "path-protected-infra", "touches a step-ca provisioner / webhook trust relationship"),
    };

    // Governance authority docs (docs/65 §3.1 must-escalate): prose by path, enforced authority by effect.
    private static readonly Regex GovernanceDoc =
        Re(@"claude-root/rules/CLAUDE\.md|(^|/)CLAUDE\.md$|autonomy-charter|62-autonomy|hard-safety");

    // wg0 on Genova by path (docs/65 §3.4 / CLAUDE.md rule 1) — content scan is the primary catch.
    private static readonly Regex Wg0 = Re(@"wg0");

    // Infra / config (docs/65 §3.4), non-trust.
    private static readonly Regex InfraPath =
        Re(@"(^|/)ansible/|(^|/)roles/|playbook|\.container$|\.volume$|quadlet|grafana|prometheus|\.rules$|systemd|\.service$|(^|/)dns/|\.zone$|deploy-controller");

    // Docs / prose (docs/65 §3.1).
    private static readonly Regex DocsPath =
        Re(@"\.(md|markdown|rst|txt)$|(^|/)docs/|(^|/)(README|JOURNAL|CHANGELOG|CONTRIBUTING)(\.|$)");

    /// <summary>Classify one path into its candidate tier + surface signals.</summary>
    public static PathClassification Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new PathClassification(null, Array.Empty<RiskSignal>()); // uncertainty → fail-safe upstream

        var p = path.Trim();

        foreach (var (rx, surface, code, desc) in TrustPlanePaths)
            if (rx.IsMatch(p))
                return Trust(surface, code, desc);

        if (Wg0.IsMatch(p))
            return new PathClassification(RiskTier.InfraConfig, new[]
            {
                new RiskSignal("path-wg0-genova", SignalEffect.HardFloorDeny, RiskTier.InfraConfig,
                    "touches wg0 on Genova — sacrosanct, never in-scope for autonomous action", TrustSurface.Wg0Genova),
            });

        if (GovernanceDoc.IsMatch(p))
            return new PathClassification(RiskTier.DocsOnly, new[]
            {
                new RiskSignal("path-governance-doc", SignalEffect.Escalate, RiskTier.DocsOnly,
                    "edits an ENFORCED governance authority doc (its words are consumed as policy)",
                    TrustSurface.GovernanceAuthorityDoc),
            });

        if (InfraPath.IsMatch(p))
            return new PathClassification(RiskTier.InfraConfig, Array.Empty<RiskSignal>());

        if (DocsPath.IsMatch(p))
            return new PathClassification(RiskTier.DocsOnly, Array.Empty<RiskSignal>());

        // Any other non-empty path is executable/product code by default.
        return new PathClassification(RiskTier.ApplicationCode, Array.Empty<RiskSignal>());
    }

    private static PathClassification Trust(TrustSurface surface, string code, string desc) =>
        new(RiskTier.TrustPlane, new[]
        {
            new RiskSignal(code, SignalEffect.TrustPlaneEscalate, RiskTier.TrustPlane, desc, surface),
        });

    private static Regex Re(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
