using Microsoft.Extensions.Logging;
using Sinapsi.DeliveryEvaluator;
using Sinapsi.Nats;

namespace DeliveryEvaluator.Host;

/// <summary>
/// The C1 delivery risk evaluator as a durable bus consumer (SHADOW / observe-only).
///
/// <para>A single durable JetStream consumer on <c>HOMELAB_AUDIT</c>, filtered to the merge/deploy
/// change subtrees (<c>homelab.git.&gt;</c> + <c>homelab.release.&gt;</c> + <c>homelab.deploy.&gt;</c>).
/// For each observed change it: parses the event into a <see cref="ChangeSet"/>
/// (<see cref="ChangeEventParser"/>), runs the DETERMINISTIC
/// <see cref="DeterministicRiskClassifier.Classify"/> (no LLM — structurally independent of any
/// author), and publishes a <see cref="DeliveryVerdictEnvelope"/> VERDICT FACT via
/// <see cref="IVerdictFactPublisher"/>.</para>
///
/// <para><b>Observe-only is structural, not a flag.</b> This worker depends only on a
/// <i>fact publisher</i>. It holds NO <c>IActCommandDispatcher</c>, constructs NO <c>ActCommand</c>,
/// and has no code path onto the <c>delivery.command.&gt;</c> act tree — the act seam stays the
/// deny-by-default <see cref="Sinapsi.Nats.EventPlane.NullActCommandDispatcher"/> (unbuilt
/// executor). A verdict is a FACT, never a trigger (<c>docs/64 §3</c>). There is no enforcement
/// knob to turn on.</para>
/// </summary>
public sealed class DeliveryEvaluatorWorker : JetStreamWorker
{
    private readonly IVerdictFactPublisher _publisher;

    public long VerdictsPublished { get; private set; }
    public long DeadLettered { get; private set; }
    public DateTimeOffset? LastVerdictAt { get; private set; }

    public DeliveryEvaluatorWorker(
        IVerdictFactPublisher publisher, NatsConnectionOptions opts, ILogger<DeliveryEvaluatorWorker> log)
        : base(opts, log)
    {
        _publisher = publisher;
    }

    protected override string StreamName =>
        Environment.GetEnvironmentVariable("EVALUATOR_STREAM") ?? "HOMELAB_AUDIT";

    protected override string DurableName =>
        Environment.GetEnvironmentVariable("EVALUATOR_DURABLE") ?? "delivery-evaluator";

    // Single-filter fallback (the base requires it); the real binding is FilterSubjects (plural).
    protected override string FilterSubject => WatchSubjects()[0];

    /// <summary>The merge/deploy change subtrees this evaluator grades. Env-driven
    /// (<c>EVALUATOR_WATCH_SUBJECTS</c>, comma-separated) so the deploy config is the single source
    /// of truth; defaults to the fleet's git/release/deploy decision trees.</summary>
    protected override IReadOnlyList<string> FilterSubjects => WatchSubjects();

    internal static string[] WatchSubjects()
    {
        var raw = Environment.GetEnvironmentVariable("EVALUATOR_WATCH_SUBJECTS");
        var subjects = (raw ?? "homelab.git.>,homelab.release.>,homelab.deploy.>")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return subjects.Length > 0 ? subjects : new[] { "homelab.git.>", "homelab.release.>", "homelab.deploy.>" };
    }

    protected override ValueTask ProcessAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        EvaluateAsync(subject, data, ct);

    /// <summary>The evaluate→publish pipeline, factored out of <see cref="ProcessAsync"/> so it is
    /// unit-testable without a live JetStream connection: (1) parse (tolerant, fail-safe) →
    /// (2) classify (deterministic, no LLM) → (3) publish a verdict FACT. It NEVER dispatches an act.</summary>
    internal async ValueTask EvaluateAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var change = ChangeEventParser.Parse(data);
        var verdict = DeterministicRiskClassifier.Classify(change);

        var factSubject = DeliveryVerdictEnvelope.SubjectFor(verdict);
        var envelope = DeliveryVerdictEnvelope.ToEnvelopeData(verdict, change.CorrelationId);

        await _publisher.PublishAsync(factSubject, envelope, ct);

        if (verdict.Unparseable) DeadLettered++;
        VerdictsPublished++;
        LastVerdictAt = DateTimeOffset.UtcNow;
        Log.LogInformation(
            "delivery verdict {verdict} (tier={tier}, conf={conf}) for {srcSubject} -> {factSubject}",
            verdict.Verdict, verdict.Tier, verdict.Confidence, subject, factSubject);
    }
}
