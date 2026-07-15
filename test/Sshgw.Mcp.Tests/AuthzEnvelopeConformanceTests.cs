using Sinapsi.Nats.EventPlane;
using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// Proves the Q2 (in-MCP) emitter's INDEPENDENTLY-built envelope conforms to the common
/// decision envelope Published Language (docs/64 §3 / docs/61 §8). The Q2 emitter constructs its
/// own <see cref="System.Text.Json.Nodes.JsonObject"/> by hand (no shared DTO); this test is the
/// per-layer conformance gate that keeps it wire-compatible with Q1/Q3 without a Shared Kernel.
/// </summary>
public sealed class AuthzEnvelopeConformanceTests
{
    [Theory]
    [InlineData("allow", "whitelist match", "systemctl status nats")]
    [InlineData("requiresApproval", "systemctl restart = mutating", "systemctl restart nats")]
    [InlineData("deny", "reads a secret path", "cat /etc/nats/admin.seed")]
    public void Q2Envelope_ConformsToThePublishedLanguage(string verdict, string reason, string cmd)
    {
        var data = AuthzDecisionPublisher.BuildEnvelope(verdict, reason, "bernex-proxmox", cmd, correlationId: "req-42");
        Assert.True(DecisionEnvelopeContract.IsConformant(data, out var errors), string.Join("; ", errors));
    }

    [Fact]
    public void Q2Envelope_DeclaresTheQ2Coordinates()
    {
        var data = AuthzDecisionPublisher.BuildEnvelope("allow", "ok", "s", "uptime", null);
        Assert.Equal("q2", (string?)data["layer"]);
        Assert.Equal("command-safety", (string?)data["question"]);
        Assert.Equal("in-mcp-authorizer", (string?)data["surface"]);
        // These three are in the contract's closed vocabularies, so conformance also asserts them.
        Assert.Contains("q2", DecisionEnvelopeContract.Layers);
        Assert.Contains("command-safety", DecisionEnvelopeContract.Questions);
        Assert.Contains("in-mcp-authorizer", DecisionEnvelopeContract.Surfaces);
    }
}
