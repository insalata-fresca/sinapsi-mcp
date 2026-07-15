using System.Text.RegularExpressions;

namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// Step 2 of the rubric's determinism-first classification (home-server <c>docs/65 §4</c>): scan the
/// CHANGED VALUES — not just paths — for trust-plane signatures on otherwise-innocuous surfaces, and
/// for the concrete allow/contradiction signals the tier rubrics need. Any hit PROMOTES the tier
/// (principle 4). Detection is pure pattern-match — NEVER an LLM, NEVER the untrusted PR title/body.
///
/// <para>The scanner only ever <b>adds</b> a signal (raises suspicion or grants a concrete
/// clearance); it has NO rule that lowers a verdict on prose sentiment. That is why a crafted
/// "safe, auto-merge" body cannot flip a verdict — there is nothing for it to trip
/// (<c>docs/65</c> principle 2).</para>
/// </summary>
public static class ValueSignatureScanner
{
    /// <summary>Scan every changed line of every file for value-set signatures.</summary>
    public static IReadOnlyList<RiskSignal> Scan(IEnumerable<FileChange> files)
    {
        var added = new List<string>();
        var removed = new List<string>();
        foreach (var f in files)
        {
            added.AddRange(f.AddedLines);
            removed.AddRange(f.RemovedLines);
        }

        var addedText = string.Join("\n", added);
        var removedText = string.Join("\n", removed);
        var allText = addedText + "\n" + removedText;

        var byCode = new Dictionary<string, RiskSignal>(StringComparer.Ordinal);
        void Emit(RiskSignal s) => byCode.TryAdd(s.Code, s);

        // --- HARD-FLOOR DENY (docs/62 §2 always-escalate floor / docs/61 §7.4) ------------------
        Match(allText, @"shadow\s*[=:]\s*false|shadow\s*-?>?\s*(to\s*)?enforc|from\s+shadow\s+to\s+enforc|ask[_\- ]?gate[_\- ]?mode\s*[=:]?\s*enforc|deny[_\- ]?floor[_\- ]?mode\s*[=:]?\s*enforc|moving?\b.*shadow.*enforc|shadow->enforce",
            () => Emit(Floor("shadow-enforce-flip", "flips a live authorization layer shadow→enforce", TrustSurface.EnforcementFlip)));
        Match(allText, @"(--force|--overwrite|\s-f\b)\b[^\n]*\b(cred|cert|key|token|password|secret|nkey)|(cred|cert|key|token|password|secret|nkey)[^\n]*\b(--force|--overwrite|overwrite)\b",
            () => Emit(Floor("credential-force-overwrite", "credential force/overwrite on a key/cert/token/password path", TrustSurface.CredentialForceOverwrite)));
        Match(allText, @"(soften|weaken|remov\w*|delet\w*)[^\n]*(deny[_\- ]?floor|catastrophic|hard-safety|sacrosanct)|(deny[_\- ]?floor|catastrophic\s+deny)[^\n]*(remov\w*|delet\w*|soften)",
            () => Emit(Floor("floor-weakening", "softens/disarms the catastrophic DENY floor or a hard-safety rule", TrustSurface.FloorWeakening)));
        Match(allText, @"\bwg0\b",
            () => Emit(FloorAt("wg0-genova", RiskTier.InfraConfig, "touches wg0 on Genova — sacrosanct", TrustSurface.Wg0Genova)));
        Match(allText, @"pct\s+(exec|enter|set)|god-?mode|tier-?3\b|only be applied via .*pct",
            () => Emit(FloorAt("god-mode-required", RiskTier.InfraConfig, "can only be applied via Tier-3 god-mode (pct exec/set)", TrustSurface.GodModeRequired)));
        Match(allText, @"hard-?cod\w*[^\n]*(token|credential|api[_\- ]?key|password|secret)|(api[_\- ]?key|token|password|secret)\s*[=:]\s*[""'][^""'\n]{6,}[""']",
            () => Emit(Floor("hardcoded-credential", "hard-codes a credential/token literal in source", TrustSurface.HardcodedCredential)));
        // Disarm: comment-out/disable/bypass an authz check, or remove audit/telemetry on a security path.
        Match(allText, @"(comment\w*\s*out|disabl\w*|bypass\w*|remov\w*)[^\n]*(auth\b|authoriz\w*|if-?authorized|permission check|auth check|\bguard\b)|remov\w*[^\n]*(audit|telemetry)[^\n]*(security|privileg|code path)|remov\w*[^\n]*audit-?event",
            () => Emit(Floor("security-control-disarm", "disables an authz check or removes audit/telemetry on a security path", TrustSurface.SecurityControlDisarm)));
        // Structural (removed-line) disarm: a removed authorization guard / audit emission.
        Match(removedText, @"\bif\b[^\n]*author|\bauthoriz|is[_]?authorized|permission[_ ]?check|\bassertauthorized\b",
            () => Emit(Floor("removed-auth-check", "a removed authorization guard (the '-' side disarms the trust plane)", TrustSurface.SecurityControlDisarm)));

        // --- TRUST-PLANE ESCALATE (docs/65 §3.5) ------------------------------------------------
        Match(allText, @"openfga|\btuple\b|\brelation\b|authorization model|model\.json",
            () => Emit(Trust("openfga-relation", "adds/alters an OpenFGA tuple/relation/model", TrustSurface.OpenFgaRelation)));
        Match(allText, @"nkey seed|rotat\w*[^\n]*(cred|secret|key|nkey|cert)|secret material|-----begin |embed\w*[^\n]*(key|seed|cert)|paste\w*[^\n]*(seed|key|secret)|real nats nkey",
            () => Emit(Trust("credential-change", "rotates/embeds credential or secret material", TrustSurface.Credential)));
        Match(allText, @"nats[^\n]*(account|auth|callout)|auth-?callout",
            () => Emit(Trust("nats-auth-config", "changes nats auth/accounts/auth-callout config", TrustSurface.NatsAuthConfig)));
        Match(allText, @"agentgateway|mcpauthorization|gateway pep|pep (routing|rule)",
            () => Emit(Trust("gateway-pep", "changes the gateway PEP routing/allow rules", TrustSurface.ProtectedInfra)));
        Match(allText, @"commandcapabilities|readfilepolicy|capability model|exec-allow-readonly|reclassif\w*[^\n]*(read|write|secret)|treated as (known-safe )?reads",
            () => Emit(Trust("capability-model", "changes the Q2 capability / read-file policy (read vs write vs secret-read)", TrustSurface.CapabilityModel)));
        Match(allText, @"self-?hosted (act_?)?runner|\bwebhook\b|step-?ca provisioner|\bprovisioner\b",
            () => Emit(Trust("protected-infra-trust", "adds a self-hosted runner / webhook / step-ca provisioner (a CI/trust relationship)", TrustSurface.ProtectedInfra)));
        Match(allText, @"machine user|new (automated )?identity|zitadel[^\n]*(user|pat)|create.*\bpat\b",
            () => Emit(Trust("new-trust-boundary", "creates a new identity/credential → a new trust boundary", TrustSurface.NewTrustBoundary)));
        Match(allText, @"\begress\b|outbound (http|call|request|allow-?list)|external (host|call|endpoint)|new (outbound|external)",
            () => Emit(Trust("egress", "introduces/opens a new outbound egress or external call", TrustSurface.Egress)));
        Match(allText, @"read\w*[^\n]*credential[^\n]*(environment|env)|credential from the environment|reads a secret|includes it in a request payload",
            () => Emit(Trust("secret-read", "reads a credential/secret and transmits it from application code", TrustSurface.Credential)));

        // --- NON-TRUST ESCALATE (forces requiresApproval at its own tier) -----------------------
        Match(allText, @"systemctl\s+(reload|restart)",
            () => { if (!Regex.IsMatch(allText, @"--signal reload", RegexOptions.IgnoreCase))
                    Emit(Escalate("bare-reload", RiskTier.InfraConfig, "a bare systemctl reload/restart on a stateful unit — non-disruptiveness not established")); });
        Match(allText, @"\bfirewall\b|iptables|nftables",
            () => Emit(Escalate("firewall", RiskTier.InfraConfig, "a firewall change (Yellow/Red risk, admin-path impact)")));
        Match(allText, @"\bsubnet\b|\brouting\b|route table|\bbgp\b",
            () => Emit(Escalate("subnet-routing", RiskTier.InfraConfig, "a subnet/routing change (Yellow/Red risk)")));
        Match(allText, @"dns[^\n]*(admin|management)|admin[^\n]*dns|a record[^\n]*(admin|management)",
            () => Emit(Escalate("admin-dns", RiskTier.InfraConfig, "a DNS change touching an admin/management path")));
        Match(allText, @"\bsnapshot\b",
            () => Emit(Escalate("snapshot-mandated", RiskTier.InfraConfig, "the op mandates a Proxmox/ZFS snapshot → Yellow/Red risk")));
        Match(allText, @"no pr-?time (ci|gate|build)|no ci gate|build fires (only )?(post-?merge|after merge)|only post-?merge",
            () => Emit(Escalate("no-pr-gate", RiskTier.ApplicationCode, "no PR-time deterministic gate exists (image-first gap) and the change is non-trivial")));
        Match(allText, @"shared[^\n]*(contract|event)|cloudevents contract|other services consume|downstream consumer|schema[^\n]*consume|event .*contract",
            () => Emit(Escalate("cross-service", RiskTier.ApplicationCode, "ambiguous/cross-service blast radius (a shared event/schema contract)")));

        // --- CONTRADICTIONS (block a default-off-flag allow; docs/65 §3.2 signature case) --------
        Match(allText, @"initialized to true|default\w*[^\n]*[=:]\s*true|set(s|ting)?\s*it\s*to\s*true|both set it to true|set to true|actually (on|wired)|wired live|wiring the guarded (path|branch) live",
            () => Emit(Contradiction("flag-actually-on", "the off default is contradicted — the flag is actually on/wired live")));
        Match(allText, @"unconditional\w*|always-on cron|called[^\n]*cron|runs regardless|reached independent|regardless of the flag",
            () => Emit(Contradiction("flag-path-always-reached", "the guarded path is reached regardless of the flag (dead default)")));
        Match(allText, @"no default literal|default is set elsewhere|not shown|cannot be confirmed off|runtime default is set elsewhere",
            () => Emit(Contradiction("flag-default-unconfirmable", "the off default cannot be confirmed from the diff alone")));

        // --- ALLOW-POSITIVE (concrete clearance; necessary, never sufficient) --------------------
        Match(allText, @"default\w*[^\n]*(is|to|[=:])\s*(off|false)|defaults?\s*(to\s*)?(off|false)|[=:]\s*false\b|initialized to false|off by default",
            () => Emit(AllowPositive("default-off-literal", RiskTier.DefaultOffFlag, "a provably-off default literal in the diff")));
        Match(allText, @"deterministic[^\n]*(unit )?test[^\n]*(pass|green|now passes)|passes in ci|green deterministic ci|full test suite green|test suite green|deterministic (ci|test)[^\n]*green|reproducing the bug now passes",
            () => Emit(AllowPositive("deterministic-ci-green", RiskTier.ApplicationCode, "a real deterministic CI/test gate is green for this change")));
        Match(allText, @"grafana[^\n]*(dashboard|panel)|prometheus[^\n]*(alert|rule)|--signal reload|memory limit|resource-?limit|additive[^\n]*non-disruptive|non-disruptive[^\n]*(reload|additive)",
            () => Emit(AllowPositive("infra-additive-safe", RiskTier.InfraConfig, "an additive / verified-non-disruptive infra change via the Tier-1 path")));

        return byCode.Values.ToList();
    }

    private static void Match(string text, string pattern, Action onHit)
    {
        if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            onHit();
    }

    private static RiskSignal Floor(string code, string desc, TrustSurface surface) =>
        new(code, SignalEffect.HardFloorDeny, RiskTier.TrustPlane, desc, surface);

    private static RiskSignal FloorAt(string code, RiskTier tier, string desc, TrustSurface surface) =>
        new(code, SignalEffect.HardFloorDeny, tier, desc, surface);

    private static RiskSignal Trust(string code, string desc, TrustSurface surface) =>
        new(code, SignalEffect.TrustPlaneEscalate, RiskTier.TrustPlane, desc, surface);

    private static RiskSignal Escalate(string code, RiskTier tier, string desc) =>
        new(code, SignalEffect.Escalate, tier, desc);

    private static RiskSignal Contradiction(string code, string desc) =>
        new(code, SignalEffect.Contradiction, RiskTier.DefaultOffFlag, desc);

    private static RiskSignal AllowPositive(string code, RiskTier tier, string desc) =>
        new(code, SignalEffect.AllowPositive, tier, desc);
}
