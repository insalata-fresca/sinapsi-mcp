using Sinapsi.Nats.EventPlane;

namespace Sinapsi.Governance.Events;

/// <summary>
/// The subject discipline for governance signals, reusing the C2/C3 event-plane rule
/// (<see cref="EventPlaneChannels"/>): a governance signal is a <b>FACT</b> — trust
/// changed, an SLO breached, an audit concurred/dissented, a red-team probe ran — with
/// MANY consumers (the Sentinel Console, Grafana, the operator) and NO single owner, and
/// it is <b>never itself a trigger</b>. Nothing auto-acts because trust decayed; a human/
/// pipeline reads the fact and decides.
///
/// <para>All governance facts live under <see cref="FactSubjectRoot"/>, inside
/// <c>homelab.&gt;</c>, so they are captured by the shared <c>HOMELAB_AUDIT</c> stream
/// exactly like the Q1/Q2/Q3 authorization verdicts — same audit spine, disjoint branch.</para>
/// </summary>
public static class GovernanceChannels
{
    /// <summary>Pub/sub root for all governance FACTS (many consumers, never a trigger).</summary>
    public const string FactSubjectRoot = "homelab.governance";

    /// <summary><c>homelab.governance.trust.&lt;changeClass&gt;.&lt;authority&gt;</c></summary>
    public static string Trust(ChangeClass changeClass, AutoProceedAuthority authority) =>
        $"{FactSubjectRoot}.trust.{Slug(changeClass.ToString())}.{Slug(authority.ToString())}";

    /// <summary><c>homelab.governance.slo.&lt;status&gt;</c></summary>
    public static string Slo(string status) => $"{FactSubjectRoot}.slo.{Slug(status)}";

    /// <summary><c>homelab.governance.audit.&lt;concurred|dissented&gt;</c></summary>
    public static string Audit(bool concurred) =>
        $"{FactSubjectRoot}.audit.{(concurred ? "concurred" : "dissented")}";

    /// <summary><c>homelab.governance.inspection.&lt;reason&gt;</c></summary>
    public static string Inspection(string reason) => $"{FactSubjectRoot}.inspection.{Slug(reason)}";

    /// <summary><c>homelab.governance.redteam.&lt;pass|fail&gt;</c></summary>
    public static string RedTeam(bool passed) => $"{FactSubjectRoot}.redteam.{(passed ? "pass" : "fail")}";

    /// <summary>True when the subject is (or is under) the governance fact root.</summary>
    public static bool IsGovernanceFactSubject(string? subject) =>
        !string.IsNullOrEmpty(subject) &&
        (subject == FactSubjectRoot || subject!.StartsWith(FactSubjectRoot + ".", StringComparison.Ordinal));

    /// <summary>
    /// Guard mirroring <see cref="EventPlaneChannels.EnsureNotFactTriggered"/>: a governance
    /// signal must be published as a FACT under <see cref="FactSubjectRoot"/>, and must never
    /// be dispatched as an act command (that would make "trust decayed" auto-fire something).
    /// </summary>
    /// <exception cref="ArgumentException">the subject is empty, not under the governance fact
    /// root, or is an act-command subject.</exception>
    public static void EnsureFact(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("governance subject is required", nameof(subject));
        if (EventPlaneChannels.IsActCommandSubject(subject))
            throw new ArgumentException(
                $"'{subject}' is an ACT COMMAND subject. A governance signal is a FACT — " +
                $"publish it under '{FactSubjectRoot}', never as a command. See docs/64 §3.",
                nameof(subject));
        if (!IsGovernanceFactSubject(subject))
            throw new ArgumentException(
                $"'{subject}' is not under the governance fact root '{FactSubjectRoot}'.",
                nameof(subject));
    }

    private static string Slug(string value) => value.ToLowerInvariant();
}
