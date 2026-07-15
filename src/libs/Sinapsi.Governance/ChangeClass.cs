namespace Sinapsi.Governance;

/// <summary>
/// The change-class a delivery decision is scored against — the unit of graduated
/// trust. These mirror, one-for-one, the risk tiers of the human-validated rubric
/// (home-server <c>docs/65-risk-rubric.md</c> §3–§4) and of the C1 evaluator's
/// <c>Sinapsi.DeliveryEvaluator.RiskTier</c>: trust ratchets/decays <b>per class</b>,
/// so proven reliability on <see cref="DocsOnly"/> never leaks authority onto
/// <see cref="TrustPlane"/>.
///
/// <para><b>Seam.</b> Governance deliberately defines its own copy rather than taking
/// a compile dependency on the (separately-owned) evaluator: the governance layer is a
/// <i>different mechanism</i> from the thing it governs (docs/64 §2, DDIA correlated-fault).
/// When C1 merges, map <c>RiskTier.&lt;X&gt; ⇒ ChangeClass.&lt;X&gt;</c> by name — the members
/// and ordinals are kept identical on purpose.</para>
/// </summary>
public enum ChangeClass
{
    /// <summary>Unclassifiable / straddles a boundary the rubric does not cover.
    /// Fail-safe: treated as the most conservative class for trust purposes.</summary>
    Unknown = 0,

    /// <summary>Diff touches nothing but documentation.</summary>
    DocsOnly = 1,

    /// <summary>A new capability shipped default-OFF behind a flag.</summary>
    DefaultOffFlag = 2,

    /// <summary>Ordinary application/product code.</summary>
    ApplicationCode = 3,

    /// <summary>Infrastructure / deployment config (non-trust-plane).</summary>
    InfraConfig = 4,

    /// <summary>The trust / security plane — OpenFGA relations, credentials, protected
    /// infra, nats/auth config. Deterministic-escalate-or-block; <b>never</b> an agent
    /// value-judgment (docs/64 §2, docs/65 principle 5). Trust here is hard-capped below
    /// the auto-proceed threshold — see <see cref="TrustLedgerConfig"/>.</summary>
    TrustPlane = 5,
}

/// <summary>Ordering + trust-plane helpers over <see cref="ChangeClass"/>.</summary>
public static class ChangeClassOrdering
{
    /// <summary>The more-severe of two classes (the higher ordinal). A change is
    /// scored at the MAXIMUM over every surface it touches (rubric principle 4).</summary>
    public static ChangeClass Max(ChangeClass a, ChangeClass b) => (int)a >= (int)b ? a : b;

    /// <summary>True for the trust/security plane, the class that can never earn auto-proceed.</summary>
    public static bool IsTrustPlane(this ChangeClass changeClass) => changeClass == ChangeClass.TrustPlane;

    /// <summary>All classes, ascending in severity — handy for seeding a full ledger / AIA set.</summary>
    public static readonly IReadOnlyList<ChangeClass> All = new[]
    {
        ChangeClass.Unknown,
        ChangeClass.DocsOnly,
        ChangeClass.DefaultOffFlag,
        ChangeClass.ApplicationCode,
        ChangeClass.InfraConfig,
        ChangeClass.TrustPlane,
    };
}
