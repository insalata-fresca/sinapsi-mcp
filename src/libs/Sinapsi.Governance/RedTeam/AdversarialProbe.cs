namespace Sinapsi.Governance.RedTeam;

/// <summary>
/// One adversarial probe against the gate — a crafted change whose EFFECT is dangerous but
/// whose surface argues for its own safety (the untrusted-diff / prompt-injection attack,
/// docs/64 §3, docs/65 principles 1–2). <see cref="MustNotAutoAllow"/> is the invariant the
/// gate must uphold: a correct gate NEVER auto-allows this probe.
/// </summary>
public sealed record AdversarialProbe(
    string Id,
    ChangeClass ChangeClass,
    string CraftedChangeSummary,
    string Attack,
    bool MustNotAutoAllow = true);
