using System.Text;

namespace Sinapsi.Nats.EventPlane;

/// <summary>The fail-safe fallback for a change the evaluator/authorizer could not classify.
/// Both values are non-permissive by construction — a classification failure NEVER yields
/// <c>allow</c>.</summary>
public enum UnclassifiedFallback
{
    /// <summary>Route to the operator's ASK gate (a write we could not prove safe).</summary>
    RequiresApproval,
    /// <summary>Refuse outright (a change we could not even parse / recognise).</summary>
    Deny,
}

/// <summary>The result of dead-lettering an unclassifiable change: the DLQ subject it was
/// routed to, the non-permissive fallback verdict, and the reason.</summary>
/// <param name="DlqSubject">Where the change was routed (under
/// <see cref="EventPlaneChannels.DeadLetterSubjectRoot"/>).</param>
/// <param name="Fallback">The fail-safe verdict (never <c>allow</c>).</param>
/// <param name="Reason">Why the change could not be classified.</param>
public sealed record DeadLetterOutcome(string DlqSubject, UnclassifiedFallback Fallback, string Reason)
{
    /// <summary>The verdict token to emit on the common decision envelope for this outcome.</summary>
    public string Verdict => Fallback == UnclassifiedFallback.RequiresApproval ? "requiresApproval" : "deny";
}

/// <summary>Sink that actually persists a dead-lettered change (a NATS publish to the DLQ
/// subject, a durable store). Kept as an interface so the routing POLICY is testable without a
/// broker, and so a real sink can be wired later without changing the policy.</summary>
public interface IDeadLetterSink
{
    /// <summary>Persist one dead-lettered change. Must be bounded (publish once) — the caller
    /// guarantees it is invoked exactly once per unclassifiable change, never in a retry loop.</summary>
    ValueTask WriteAsync(DeadLetterOutcome outcome, string changeRef, CancellationToken ct = default);
}

/// <summary>
/// Encodes the canon's "DLQ + deny-by-default" rule (home-server <c>docs/64 §3</c>): when the
/// evaluator cannot classify a change, route it to a dead-letter path AND default to
/// <c>requiresApproval</c>/<c>deny</c> — <b>never silent-drop, never allow, never infinite-retry</b>.
///
/// <para><see cref="Route"/> is a PURE decision: it derives the DLQ subject + the fail-safe
/// verdict and returns them, so the "cannot classify ⇒ non-permissive" property is unit-testable
/// without a bus. Persisting the outcome is the sink's job (<see cref="IDeadLetterSink"/>), and
/// <see cref="RouteAsync"/> calls it EXACTLY ONCE — there is no retry loop here by design (a
/// retry storm on an unclassifiable change is itself a failure mode the canon names).</para>
/// </summary>
public static class DeadLetterRouter
{
    /// <summary>Pure: classify-failure ⇒ (DLQ subject, fail-safe verdict). Never returns allow.</summary>
    /// <param name="reason">Why classification failed (becomes the DLQ subject slug + the envelope reason).</param>
    /// <param name="fallback">The non-permissive fallback (default <see cref="UnclassifiedFallback.Deny"/> —
    /// the strongest refusal; pass <see cref="UnclassifiedFallback.RequiresApproval"/> when the change is a
    /// recognised-but-unproven write that should elevate rather than hard-fail).</param>
    public static DeadLetterOutcome Route(string reason, UnclassifiedFallback fallback = UnclassifiedFallback.Deny)
    {
        var slug = Slug(reason);
        var subject = $"{EventPlaneChannels.DeadLetterSubjectRoot}.{slug}";
        return new DeadLetterOutcome(subject, fallback, string.IsNullOrWhiteSpace(reason) ? "unclassifiable" : reason);
    }

    /// <summary>Route AND persist, exactly once. The single <see cref="IDeadLetterSink.WriteAsync"/>
    /// call is the whole "never silent-drop": an unclassifiable change is always written before the
    /// caller returns the non-permissive verdict. No retry is attempted here — a sink failure is the
    /// sink's concern (it may be durable/at-least-once internally), not a loop in the hot path.</summary>
    public static async ValueTask<DeadLetterOutcome> RouteAsync(
        IDeadLetterSink sink, string changeRef, string reason,
        UnclassifiedFallback fallback = UnclassifiedFallback.Deny, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var outcome = Route(reason, fallback);
        await sink.WriteAsync(outcome, changeRef, ct);   // exactly once — never silent-drop, never retried here
        return outcome;
    }

    // Subject tokens must be metachar-free; fold the reason to a short lowercase slug.
    internal static string Slug(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "unclassifiable";
        var sb = new StringBuilder(reason.Length);
        bool lastDash = false;
        foreach (var c in reason.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); lastDash = false; }
            else if (!lastDash) { sb.Append('-'); lastDash = true; }
            if (sb.Length >= 48) break;
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "unclassifiable" : s;
    }
}
