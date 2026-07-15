namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// The INDEPENDENT delivery risk evaluator (home-server <c>docs/64 §2-3</c>, Mission C1).
///
/// <para><b>What makes it independent.</b> It is a DETERMINISTIC classifier — pure path/content
/// pattern-match, NO LLM — so it is <i>structurally a different mechanism</i> from any agent that
/// makes or advocates a change. A second LLM pass would be a correlated fault (<c>DDIA</c> Ch8) and
/// is not an independent vote (<c>docs/64 §2</c>); this evaluator shares no reasoning with the
/// author. For trust-plane / high-blast-radius surfaces the verdict is deterministic-escalate-or-
/// deny, never an agent value-judgment (<c>docs/64 §2</c>, <c>docs/65</c> principle 5).</para>
///
/// <para><b>The four guarantees (all structural, all tested):</b></para>
/// <list type="number">
///   <item>Tier is computed by <see cref="PathTierClassifier"/> (paths) + <see cref="ValueSignatureScanner"/>
///     (values), tier = MAX over surfaces (<c>docs/65 §4</c>).</item>
///   <item>A trust-plane tier NEVER yields <see cref="Verdict.Allow"/> — the switch arm has no
///     allow branch (<c>docs/65</c> principle 5).</item>
///   <item>Fail-safe default is <see cref="Verdict.RequiresApproval"/>: every non-clearance path,
///     every uncertainty, every unparseable input escalates; <see cref="Verdict.Allow"/> is only
///     produced by an explicit positive-clearance branch (<c>docs/64 §3</c>).</item>
///   <item>The untrusted PR title/body (<see cref="ChangeSet.Metadata"/>) is never read — verdicts
///     derive only from <see cref="ChangeSet.Files"/> (<c>docs/65</c> principle 2).</item>
/// </list>
/// </summary>
public static class DeterministicRiskClassifier
{
    /// <summary>Evaluate a change and return its verdict. Never throws for a well-formed
    /// <see cref="ChangeSet"/>; a null/unparseable input is fail-safe escalated + dead-lettered.</summary>
    public static RiskVerdict Classify(ChangeSet? change)
    {
        // (0) Fail-safe: unparseable / null → requiresApproval, dead-letter, never allow.
        if (change is null || change.IsUnparseable)
        {
            return new RiskVerdict(
                Verdict.RequiresApproval, RiskTier.Unknown, Confidence.Low,
                "change could not be parsed into any effect surface — fail-safe escalate + dead-letter (docs/65 principle 3, docs/61 §8)",
                Array.Empty<TrustSurface>(),
                new[] { new RiskSignal("unparseable", SignalEffect.Escalate, RiskTier.Unknown, "no parseable file changes") },
                Unparseable: true);
        }

        var signals = new List<RiskSignal>();

        // (1) Path-set → candidate tiers + surface signals. NOTE: we read change.Files ONLY —
        //     change.Metadata (the untrusted PR title/body) is never consulted.
        var tiers = new List<RiskTier>();
        foreach (var file in change.Files)
        {
            var pc = PathTierClassifier.Classify(file.Path);
            if (pc.Tier is { } t) tiers.Add(t);
            signals.AddRange(pc.Signals);
        }

        // (2) Value-set → overrides/promotions (trust-plane signatures on innocuous paths, and the
        //     concrete allow/contradiction signals).
        signals.AddRange(ValueSignatureScanner.Scan(change.Files));

        // Fold in every signal's implied tier, then (3) tier = MAX over surfaces.
        tiers.AddRange(signals.Select(s => s.ImpliedTier));
        var tier = tiers.Count == 0 ? RiskTier.Unknown : tiers.Aggregate(RiskTier.Unknown, RiskTierOrdering.Max);

        // (4) Apply the tier's rubric → verdict + confidence.
        var (verdict, confidence, reason) = DeriveVerdict(tier, signals);

        var surfaces = signals.Where(s => s.Surface is not null)
            .Select(s => s.Surface!.Value).Distinct().ToList();

        return new RiskVerdict(verdict, tier, confidence, reason, surfaces, Dedupe(signals));
    }

    private static (Verdict, Confidence, string) DeriveVerdict(RiskTier tier, List<RiskSignal> signals)
    {
        // Hard floor first: any always-escalate-floor / welded-shut item → deny, regardless of tier.
        var floor = signals.FirstOrDefault(s => s.Effect == SignalEffect.HardFloorDeny);
        if (floor is not null)
            return (Verdict.Deny, Confidence.High,
                $"deny (hard floor): {floor.Description} — the auto-pipeline is not an authorized actor (docs/62 §2, docs/61 §7.4)");

        // Trust plane: NO agent-cleared allow exists — structurally only requiresApproval remains here.
        if (tier.IsTrustPlane())
        {
            var s = signals.FirstOrDefault(x => x.Effect == SignalEffect.TrustPlaneEscalate);
            return (Verdict.RequiresApproval, Confidence.High,
                $"requiresApproval (trust plane): {s?.Description ?? "a trust/security-plane surface is touched"} — deterministic-escalate, never an agent value-judgment (docs/64 §2, docs/65 §3.5)");
        }

        // Non-trust escalate reasons (infra disruptive / no-gate / cross-service …) → requiresApproval.
        var esc = signals.FirstOrDefault(s => s.Effect == SignalEffect.Escalate);
        if (esc is not null)
            return (Verdict.RequiresApproval, Confidence.Medium,
                $"requiresApproval: {esc.Description} (docs/65 §3.{TierSection(tier)})");

        // A contradiction of an allow-criterion (a default-off flag that is actually on) → escalate.
        var contra = signals.FirstOrDefault(s => s.Effect == SignalEffect.Contradiction);
        if (contra is not null)
            return (Verdict.RequiresApproval, Confidence.Medium,
                $"requiresApproval: {contra.Description} — effect over declaration (docs/65 principle 1, §3.2)");

        // Reached here → no floor, not trust plane, no escalate/contradiction. Allow ONLY on a
        // positive, tier-appropriate clearance; otherwise fail-safe escalate.
        var hasAllowPositive = signals.Any(s => s.Effect == SignalEffect.AllowPositive);
        return tier switch
        {
            RiskTier.DocsOnly =>
                (Verdict.Allow, Confidence.High,
                 "allow: docs-only by path, no live secret/policy value, not an enforced authority doc (docs/65 §3.1)"),

            RiskTier.DefaultOffFlag when hasAllowPositive =>
                (Verdict.Allow, Confidence.Medium,
                 "allow: default provably off in-diff, guarded branch unreached, no trust-plane effect (docs/65 §3.2)"),

            RiskTier.ApplicationCode when hasAllowPositive =>
                (Verdict.Allow, Confidence.Medium,
                 "allow: a real deterministic CI/test gate is green, single-service blast radius, no trust surface (docs/65 §3.3)"),

            RiskTier.InfraConfig when hasAllowPositive =>
                (Verdict.Allow, Confidence.Medium,
                 "allow: additive / verified-non-disruptive infra via the Tier-1 path (docs/65 §3.4)"),

            // No positive clearance for the tier, or Unknown → fail-safe escalate.
            _ => (Verdict.RequiresApproval, Confidence.Low,
                 $"requiresApproval (fail-safe default): {tier} change could not be positively cleared — uncertainty escalates, never auto-allows (docs/64 §3, docs/65 principle 3)"),
        };
    }

    private static string TierSection(RiskTier tier) => tier switch
    {
        RiskTier.DocsOnly => "1",
        RiskTier.DefaultOffFlag => "2",
        RiskTier.ApplicationCode => "3",
        RiskTier.InfraConfig => "4",
        RiskTier.TrustPlane => "5",
        _ => "3",
    };

    private static IReadOnlyList<RiskSignal> Dedupe(List<RiskSignal> signals)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RiskSignal>();
        foreach (var s in signals)
            if (seen.Add(s.Code))
                result.Add(s);
        return result;
    }
}
