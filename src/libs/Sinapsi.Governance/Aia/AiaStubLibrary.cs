namespace Sinapsi.Governance.Aia;

/// <summary>
/// The per-tier AIA stub library. Every change class has a stub; the trust-plane stub pins
/// <see cref="AlgorithmicImpactAssessment.AutomationPermitted"/> = false (consistent with the
/// deterministic-escalate invariant and the trust-ledger hard cap), so the AIA and the trust
/// ledger agree that the trust plane is never fully auto.
/// </summary>
public static class AiaStubLibrary
{
    /// <summary>The AIA stub for a change class (never null — an unknown class gets the most conservative stub).</summary>
    public static AlgorithmicImpactAssessment For(ChangeClass changeClass) => changeClass switch
    {
        ChangeClass.DocsOnly => new(changeClass,
            DecisionScope: "auto-proceed on documentation-only changes",
            PotentialHarms: "misleading docs; negligible system risk",
            HumanOversight: "retrospective inspection sample only",
            Reversibility: "trivially reversible (revert commit)",
            FallbackOnUncertainty: "requiresApproval",
            AutomationPermitted: true),

        ChangeClass.DefaultOffFlag => new(changeClass,
            DecisionScope: "auto-proceed on default-OFF flagged capabilities",
            PotentialHarms: "dormant code path; risk only on later flag flip",
            HumanOversight: "retrospective inspection + flag-flip is a separate governed change",
            Reversibility: "reversible (flag stays off; revert)",
            FallbackOnUncertainty: "requiresApproval",
            AutomationPermitted: true),

        ChangeClass.ApplicationCode => new(changeClass,
            DecisionScope: "auto-proceed on application/product code once trust is earned",
            PotentialHarms: "product regressions; bounded blast radius",
            HumanOversight: "trust ledger + escalation SLO + verify/bake window (C3) + retrospective sample",
            Reversibility: "conditional (compensator reachable + downstream hasn't acted; C3)",
            FallbackOnUncertainty: "requiresApproval",
            AutomationPermitted: true),

        ChangeClass.InfraConfig => new(changeClass,
            DecisionScope: "auto-proceed on infra/deploy config where a deterministic CI gate exists",
            PotentialHarms: "service disruption; wider blast radius",
            HumanOversight: "trust ledger + SLO + synthetic-monitoring/bake gate + daily North-Star",
            Reversibility: "conditional; rollback path untrusted until drilled (C3)",
            FallbackOnUncertainty: "requiresApproval",
            AutomationPermitted: true),

        // Trust plane + unknown: automation NOT permitted — always escalate/deny.
        _ => new(changeClass,
            DecisionScope: "the trust/security plane (OpenFGA, credentials, protected infra, nats/auth)",
            PotentialHarms: "trust-boundary compromise; catastrophic, may be irreversible",
            HumanOversight: "MANDATORY human — deterministic escalate-or-block, independent audit, named owner",
            Reversibility: "assume irreversible; welded-shut deny floor (docs/61 §7.4)",
            FallbackOnUncertainty: "deny / requiresApproval — never allow by agent judgment",
            AutomationPermitted: false),
    };

    /// <summary>The full per-tier stub set, one per change class.</summary>
    public static IReadOnlyList<AlgorithmicImpactAssessment> All =>
        ChangeClassOrdering.All.Select(For).ToList();
}
