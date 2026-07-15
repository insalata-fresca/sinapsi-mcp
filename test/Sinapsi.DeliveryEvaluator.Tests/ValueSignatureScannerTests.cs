using Xunit;

namespace Sinapsi.DeliveryEvaluator.Tests;

public class ValueSignatureScannerTests
{
    private static IReadOnlyList<RiskSignal> ScanLine(string line) =>
        ValueSignatureScanner.Scan(new[] { FileChange.Added_("x", line) });

    [Theory]
    [InlineData("SHADOW=false", TrustSurface.EnforcementFlip)]
    [InlineData("ASK_GATE_MODE=enforce", TrustSurface.EnforcementFlip)]
    [InlineData("rotate the cert with --force overwrite", TrustSurface.CredentialForceOverwrite)]
    [InlineData("delete a dead rule from the catastrophic DENY-floor list", TrustSurface.FloorWeakening)]
    [InlineData("apply via pct exec on the container", TrustSurface.GodModeRequired)]
    [InlineData("hard-code the api token as a default", TrustSurface.HardcodedCredential)]
    public void hard_floor_signatures_are_detected(string line, TrustSurface surface) =>
        Assert.Contains(ScanLine(line), s => s.Effect == SignalEffect.HardFloorDeny && s.Surface == surface);

    [Fact]
    public void a_removed_authorization_guard_is_a_hard_floor()
    {
        var change = new FileChange("src/Svc.cs", ChangeKind.Modified,
            AddedLines: Array.Empty<string>(),
            RemovedLines: new[] { "if (isAuthorized(user)) {" });
        Assert.Contains(ValueSignatureScanner.Scan(new[] { change }),
            s => s.Effect == SignalEffect.HardFloorDeny && s.Surface == TrustSurface.SecurityControlDisarm);
    }

    [Theory]
    [InlineData("adds a new OpenFGA relation", TrustSurface.OpenFgaRelation)]
    [InlineData("pastes a real NATS nkey seed value", TrustSurface.Credential)]
    [InlineData("changes an nats auth-callout account permission", TrustSurface.NatsAuthConfig)]
    [InlineData("adds a new outbound HTTP call to an external host", TrustSurface.Egress)]
    public void trust_plane_value_signatures_promote(string line, TrustSurface surface) =>
        Assert.Contains(ScanLine(line), s => s.Effect == SignalEffect.TrustPlaneEscalate && s.Surface == surface);

    [Fact]
    public void a_default_off_literal_is_an_allow_positive() =>
        Assert.Contains(ScanLine("whose default literal is false"),
            s => s.Effect == SignalEffect.AllowPositive && s.ImpliedTier == RiskTier.DefaultOffFlag);

    [Fact]
    public void a_flag_actually_on_is_a_contradiction() =>
        Assert.Contains(ScanLine("the flag's default literal in code is initialized to true"),
            s => s.Effect == SignalEffect.Contradiction);

    [Fact]
    public void a_bare_reload_escalates_but_signal_reload_does_not()
    {
        Assert.Contains(ScanLine("reload with systemctl reload nats-server"),
            s => s.Code == "bare-reload" && s.Effect == SignalEffect.Escalate);
        Assert.DoesNotContain(ScanLine("reload with nats-server --signal reload"),
            s => s.Code == "bare-reload");
    }
}
