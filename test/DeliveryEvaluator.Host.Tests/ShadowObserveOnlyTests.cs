using System.Text;
using DeliveryEvaluator.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Sinapsi.Nats;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace DeliveryEvaluator.Host.Tests;

/// <summary>
/// The structural proof that the host is SHADOW / observe-only: every subject it publishes is a
/// verdict FACT (or a dead-letter) and NEVER an act command; the act seam is the deny-by-default
/// <see cref="NullActCommandDispatcher"/>; and the fact-not-act guard fail-closes.
/// </summary>
public class ShadowObserveOnlyTests
{
    private static DeliveryEvaluatorWorker NewWorker(RecordingPublisher pub) =>
        new(pub, new NatsConnectionOptions(), NullLogger<DeliveryEvaluatorWorker>.Instance);

    private static ReadOnlyMemory<byte> Bytes(string json) => Encoding.UTF8.GetBytes(json);

    public static IEnumerable<object[]> RepresentativeEvents() => new[]
    {
        // A clean docs-only change → allow (still a FACT, never an act).
        new object[] { """{"id":"e1","data":{"files":[{"path":"docs/notes.md"}]}}""" },
        // An OpenFGA relation in changed content → trust-plane → requiresApproval.
        new object[] { """{"id":"e2","data":{"config_default":"authorization_model openfga tuple relation\n"}}""" },
        // A shadow→enforce flip in changed content → hard-floor deny.
        new object[] { """{"id":"e3","data":{"config_default":"SHADOW=false\n"}}""" },
        // An unclassifiable event → dead-letter.
        new object[] { """{"id":"e4","data":{"note":"nothing"}}""" },
        // Malformed → dead-letter.
        new object[] { "{ not json" },
    };

    [Theory]
    [MemberData(nameof(RepresentativeEvents))]
    public async Task Every_published_subject_is_a_fact_or_deadletter_never_an_act_command(string json)
    {
        var pub = new RecordingPublisher();
        var worker = NewWorker(pub);

        await worker.EvaluateAsync("homelab.git.repo.push", Bytes(json), CancellationToken.None);

        var (subject, _) = Assert.Single(pub.Published);
        Assert.False(EventPlaneChannels.IsActCommandSubject(subject),
            $"observe-only violated: published to act-command subject '{subject}'");
        Assert.True(
            EventPlaneChannels.IsVerdictFactSubject(subject) || EventPlaneChannels.IsDeadLetterSubject(subject),
            $"'{subject}' must be a verdict-fact or dead-letter subject");
    }

    [Fact]
    public async Task Openfga_change_escalates_to_requires_approval_fact()
    {
        var pub = new RecordingPublisher();
        var worker = NewWorker(pub);

        await worker.EvaluateAsync("homelab.deploy.foo.applied",
            Bytes("""{"id":"e","data":{"config_default":"openfga relation tuple\n"}}"""), CancellationToken.None);

        var (subject, _) = Assert.Single(pub.Published);
        Assert.Equal("homelab.security.authz.delivery-evaluator.requires-approval.delivery-risk-evaluator", subject);
    }

    [Fact]
    public void Publisher_guard_fail_closes_on_an_act_command_subject()
    {
        // Even a future coding mistake that computed an act-command subject cannot emit an act.
        Assert.Throws<InvalidOperationException>(() =>
            NatsVerdictFactPublisher.EnsureFactNotAct($"{EventPlaneChannels.ActCommandSubjectRoot}.merge-pr"));
    }

    [Fact]
    public async Task Act_seam_is_deny_by_default_null_dispatcher()
    {
        // The act path the evaluator would never dispatch on is the deny-by-default NullActCommandDispatcher:
        // it REJECTS every command. This documents the welded-shut act seam (docs/64 §3).
        IActCommandDispatcher seam = new NullActCommandDispatcher();
        var command = new ActCommand("cmd-1", ActCommandKind.MergePullRequest, "ste/sinapsi-mcp#1",
            CorrelationId: "corr", RequestedBy: "delivery-evaluator", Reason: "n/a");

        var ack = await seam.DispatchAsync(command);

        Assert.False(ack.Accepted);
        Assert.Equal(ActCommandDisposition.Rejected, ack.Disposition);
    }
}
