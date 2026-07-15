using Xunit;

namespace Sinapsi.DeliveryEvaluator.Tests;

public class PathTierClassifierTests
{
    [Theory]
    [InlineData("policies/openfga/tuples.json", TrustSurface.OpenFgaRelation)]
    [InlineData("infra/secrets/service.key", TrustSurface.Credential)]
    [InlineData("etc/step-ca/root.pem", TrustSurface.Credential)]
    [InlineData("gateway/agentgateway/config.yaml", TrustSurface.ProtectedInfra)]
    [InlineData("nats/accounts.conf", TrustSurface.NatsAuthConfig)]
    [InlineData("services/claude-root/hooks/ask-gate.sh", TrustSurface.ProtectedInfra)]
    [InlineData("src/Authz/CommandCapabilities.cs", TrustSurface.CapabilityModel)]
    public void trust_plane_paths_classify_as_trust_plane(string path, TrustSurface expected)
    {
        var pc = PathTierClassifier.Classify(path);
        Assert.Equal(RiskTier.TrustPlane, pc.Tier);
        Assert.Contains(pc.Signals, s => s.Surface == expected && s.Effect == SignalEffect.TrustPlaneEscalate);
    }

    [Theory]
    [InlineData("services/claude-root/rules/CLAUDE.md")]
    [InlineData("docs/62-autonomy-charter.md")]
    public void governance_authority_docs_escalate(string path)
    {
        var pc = PathTierClassifier.Classify(path);
        Assert.Equal(RiskTier.DocsOnly, pc.Tier);
        Assert.Contains(pc.Signals, s => s.Surface == TrustSurface.GovernanceAuthorityDoc && s.Effect == SignalEffect.Escalate);
    }

    [Theory]
    [InlineData("ansible/roles/nats/tasks/main.yml")]
    [InlineData("deploy/service.container")]
    [InlineData("observability/prometheus/alerts.rules")]
    public void infra_paths_classify_as_infra(string path) =>
        Assert.Equal(RiskTier.InfraConfig, PathTierClassifier.Classify(path).Tier);

    [Theory]
    [InlineData("docs/00-overview.md")]
    [InlineData("README.md")]
    [InlineData("JOURNAL.md")]
    public void docs_paths_classify_as_docs(string path) =>
        Assert.Equal(RiskTier.DocsOnly, PathTierClassifier.Classify(path).Tier);

    [Fact]
    public void plain_source_defaults_to_application_code() =>
        Assert.Equal(RiskTier.ApplicationCode, PathTierClassifier.Classify("src/Catalog/Materialiser.cs").Tier);

    [Fact]
    public void empty_path_is_unknown_not_a_silent_pass() =>
        Assert.Null(PathTierClassifier.Classify("").Tier);

    [Fact]
    public void wg0_path_is_a_hard_floor()
    {
        var pc = PathTierClassifier.Classify("network/wg0.conf");
        Assert.Contains(pc.Signals, s => s.Effect == SignalEffect.HardFloorDeny && s.Surface == TrustSurface.Wg0Genova);
    }
}
