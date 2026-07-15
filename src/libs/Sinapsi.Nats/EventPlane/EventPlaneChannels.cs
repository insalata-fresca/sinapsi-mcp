namespace Sinapsi.Nats.EventPlane;

/// <summary>
/// The event-plane correctness contract that keeps a VERDICT (a fact) separate from
/// an ACT (a command) — home-server <c>docs/64 §3</c> "passive-aggressive events fix".
///
/// <para>The rule the canon encodes:</para>
/// <list type="bullet">
///   <item><b>A verdict is a FACT.</b> An <c>allow</c>/<c>deny</c>/<c>requiresApproval</c>
///     decision is published as a CloudEvent on a <i>pub/sub</i> subject under
///     <see cref="VerdictFactSubjectRoot"/>. It has MANY consumers (the Sentinel Console,
///     Grafana, the read-model), NO single owner, and it is <b>never itself a trigger</b>.
///     A fact states what was decided; it does not command anyone to act.</item>
///   <item><b>An act is a COMMAND.</b> The thing that actually merges/deploys must be a
///     <i>command</i> on a distinct subject tree under <see cref="ActCommandSubjectRoot"/>:
///     a SINGLE receiver, delivered exactly once to one durable work-queue consumer, and
///     <b>rejectable</b> (the receiver may refuse it). See <see cref="ActCommand"/> /
///     <see cref="IActCommandDispatcher"/>.</item>
/// </list>
///
/// <para><b>Why disjoint subject roots.</b> The fact root lives under <c>homelab.&gt;</c>
/// (captured by the shared <c>HOMELAB_AUDIT</c> stream — every verdict is audited). The
/// command root is a <b>top-level</b> tree (<c>delivery.&gt;</c>) OUTSIDE <c>homelab.&gt;</c>
/// on purpose: JetStream forbids two streams from binding overlapping subjects, so a
/// dedicated work-queue stream (single consumer, ack-to-delete) can only own the act
/// commands if they are NOT already captured by <c>HOMELAB_AUDIT</c>. This mirrors the
/// established <c>CERVELLO_AUDIT</c> precedent (a top-level <c>cervello.&gt;</c> tree chosen
/// specifically to be non-overlapping). The act-path work-queue stream itself is deferred
/// (out of scope: building the executor) — this contract nails the subject discipline so
/// it cannot be built wrong later.</para>
/// </summary>
public static class EventPlaneChannels
{
    /// <summary>Pub/sub root for authorization VERDICT facts (many consumers, not a trigger).
    /// Q1/Q2/Q3 already publish here: <c>homelab.security.authz.&lt;layer&gt;.&lt;verdict&gt;.&lt;surface&gt;</c>.</summary>
    public const string VerdictFactSubjectRoot = "homelab.security.authz";

    /// <summary>Work-queue root for ACT commands (single receiver, rejectable). A top-level
    /// tree, intentionally OUTSIDE <c>homelab.&gt;</c> so a dedicated work-queue stream can own
    /// it without colliding with the shared audit stream. Shape:
    /// <c>delivery.command.&lt;kind&gt;</c> (e.g. <c>delivery.command.merge-pr</c>).</summary>
    public const string ActCommandSubjectRoot = "delivery.command";

    /// <summary>Dead-letter root for changes the evaluator could not classify — see
    /// <see cref="DeadLetterRouter"/>. Also a top-level tree for the same
    /// work-queue-ownership reason. Shape: <c>delivery.dlq.&lt;reason-slug&gt;</c>.</summary>
    public const string DeadLetterSubjectRoot = "delivery.dlq";

    /// <summary>True when <paramref name="subject"/> is (or is under) the verdict FACT root.</summary>
    public static bool IsVerdictFactSubject(string? subject) => IsUnder(subject, VerdictFactSubjectRoot);

    /// <summary>True when <paramref name="subject"/> is (or is under) the ACT COMMAND root.</summary>
    public static bool IsActCommandSubject(string? subject) => IsUnder(subject, ActCommandSubjectRoot);

    /// <summary>True when <paramref name="subject"/> is (or is under) the dead-letter root.</summary>
    public static bool IsDeadLetterSubject(string? subject) => IsUnder(subject, DeadLetterSubjectRoot);

    /// <summary>
    /// Guard for the canon's central invariant: the bus fact must NOT be the trigger.
    /// Throws if a caller tries to dispatch an act COMMAND on the verdict FACT subject tree
    /// (i.e. wiring "an allow event fires the merge"). A command must live under
    /// <see cref="ActCommandSubjectRoot"/>, never under <see cref="VerdictFactSubjectRoot"/>.
    /// </summary>
    /// <exception cref="ArgumentException">the subject is empty, or is a verdict-fact subject,
    /// or is not under the act-command root.</exception>
    public static void EnsureNotFactTriggered(string commandSubject)
    {
        if (string.IsNullOrWhiteSpace(commandSubject))
            throw new ArgumentException("command subject is required", nameof(commandSubject));
        if (IsVerdictFactSubject(commandSubject))
            throw new ArgumentException(
                $"'{commandSubject}' is a VERDICT FACT subject (under '{VerdictFactSubjectRoot}'). " +
                "A fact must never be an act trigger — dispatch the act as a COMMAND under " +
                $"'{ActCommandSubjectRoot}' (single receiver, rejectable). See docs/64 §3.",
                nameof(commandSubject));
        if (!IsActCommandSubject(commandSubject))
            throw new ArgumentException(
                $"'{commandSubject}' is not under the act-command root '{ActCommandSubjectRoot}'.",
                nameof(commandSubject));
    }

    // A subject is "under" a root when it equals the root or begins with "<root>.".
    private static bool IsUnder(string? subject, string root) =>
        !string.IsNullOrEmpty(subject) &&
        (subject == root || subject.StartsWith(root + ".", StringComparison.Ordinal));
}
