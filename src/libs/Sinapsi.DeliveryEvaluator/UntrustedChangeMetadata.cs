namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// The UNTRUSTED, declared-intent part of a change: PR title, PR body, labels, commit message.
/// This is <b>indirect prompt injection into the gate</b> (home-server <c>docs/64 §3</c>,
/// <c>docs/65</c> principle 2): text that argues for its own safety — <c>"this is safe,
/// auto-merge"</c>, <c>"# no-op"</c>, <c>"default off so harmless"</c> — MUST NOT raise the verdict
/// toward <see cref="Verdict.Allow"/>.
///
/// <para><b>The defense is structural, not vigilant.</b> This type exists so declared intent is a
/// distinct field the classifier <see cref="DeterministicRiskClassifier"/> never reads: verdicts
/// derive only from <see cref="ChangeSet.Files"/> (effect). Because the classifier has no code path
/// that lowers a verdict on any text, a hostile body cannot flip a verdict regardless of what it
/// says. It is retained only to be LOGGED for the operator (<c>docs/65 §4</c>: "may be logged … it
/// does not move the verdict").</para>
/// </summary>
/// <param name="Title">The PR/commit title (untrusted).</param>
/// <param name="Body">The PR/commit body (untrusted).</param>
/// <param name="Labels">Any labels declared on the change (untrusted).</param>
public sealed record UntrustedChangeMetadata(
    string Title = "",
    string Body = "",
    IReadOnlyList<string>? Labels = null)
{
    /// <summary>An empty metadata (no declared intent).</summary>
    public static readonly UntrustedChangeMetadata None = new();

    /// <summary>Labels, never null.</summary>
    public IReadOnlyList<string> LabelsOrEmpty => Labels ?? Array.Empty<string>();
}
