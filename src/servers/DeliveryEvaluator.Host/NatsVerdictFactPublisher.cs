using System.Text.Json.Nodes;
using Sinapsi.Nats;
using Sinapsi.Nats.EventPlane;

namespace DeliveryEvaluator.Host;

/// <summary>
/// The NATS-backed <see cref="IVerdictFactPublisher"/>: wraps a <see cref="NatsEventPublisher"/>
/// (NKey+TLS, CloudEvents v1.0) held open for the process lifetime.
///
/// <para><b>Belt-and-braces observe-only guard.</b> Before every publish it re-proves the subject
/// is a verdict FACT or a dead-letter subject and NOT an act command — so even a future coding
/// mistake that computed a <c>delivery.command.&gt;</c> subject would throw here rather than emit an
/// act. This is defence-in-depth on top of the structural facts that the host holds no dispatcher
/// and the NATS identity is publish-scoped to <c>homelab.security.authz.&gt;</c> at the server ACL.</para>
/// </summary>
public sealed class NatsVerdictFactPublisher : IVerdictFactPublisher, IAsyncDisposable
{
    private readonly NatsEventPublisher _publisher;

    private NatsVerdictFactPublisher(NatsEventPublisher publisher) => _publisher = publisher;

    /// <summary>Connect (NKey+TLS) and return a ready publisher. <paramref name="source"/> is the
    /// CloudEvents producer URI.</summary>
    public static async Task<NatsVerdictFactPublisher> ConnectAsync(
        NatsConnectionOptions opts, string source, CancellationToken ct = default)
    {
        var publisher = await NatsEventPublisher.ConnectAsync(opts, source, ct);
        return new NatsVerdictFactPublisher(publisher);
    }

    public async ValueTask PublishAsync(string subject, JsonObject data, CancellationToken ct = default)
    {
        EnsureFactNotAct(subject);
        await _publisher.PublishAsync(subject, data, subjectAttr: subject, ct: ct);
    }

    /// <summary>Fail-closed: a subject that is (or is under) the act-command root can NEVER be
    /// published by this observe-only host. Throws before any bus write.</summary>
    internal static void EnsureFactNotAct(string subject)
    {
        if (EventPlaneChannels.IsActCommandSubject(subject))
            throw new InvalidOperationException(
                $"observe-only violation: '{subject}' is an ACT COMMAND subject (under " +
                $"'{EventPlaneChannels.ActCommandSubjectRoot}'). The delivery evaluator publishes " +
                "verdict FACTS only and must never dispatch an act (docs/64 §3).");
        if (!EventPlaneChannels.IsVerdictFactSubject(subject) && !EventPlaneChannels.IsDeadLetterSubject(subject))
            throw new InvalidOperationException(
                $"'{subject}' is neither a verdict-fact subject (under " +
                $"'{EventPlaneChannels.VerdictFactSubjectRoot}') nor a dead-letter subject (under " +
                $"'{EventPlaneChannels.DeadLetterSubjectRoot}').");
    }

    public async ValueTask DisposeAsync() => await _publisher.DisposeAsync();
}
