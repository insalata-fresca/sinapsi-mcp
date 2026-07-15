namespace Sinapsi.Governance.RedTeam;

/// <summary>
/// The result of running one probe through the gate. <see cref="Held"/> = the gate upheld
/// the invariant (did not auto-allow a probe that <see cref="AdversarialProbe.MustNotAutoAllow"/>).
/// A false (breach) is the finding that must decay/revoke trust and reach the accountable owner.
/// </summary>
public sealed record RedTeamFinding(AdversarialProbe Probe, bool GateAutoAllowed, bool Held, string Note);
