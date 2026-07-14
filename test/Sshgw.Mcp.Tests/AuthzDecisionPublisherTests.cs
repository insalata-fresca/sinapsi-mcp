using System.Text.Json.Nodes;
using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// Unit tests for the Q2 decision envelope (<see cref="AuthzDecisionPublisher.BuildEnvelope"/>).
/// The publish transport (NATS) is not exercised here — only the payload shape the control
/// plane consumes: verdict, reason, verb extraction, command scrub+truncate, correlation id.
/// </summary>
public sealed class AuthzDecisionPublisherTests
{
    private static JsonObject Env(string verdict, string reason, string? server, string cmd, string? corr = null)
        => AuthzDecisionPublisher.BuildEnvelope(verdict, reason, server, cmd, corr);

    [Theory]
    [InlineData("allow")]
    [InlineData("requiresApproval")]
    [InlineData("deny")]
    public void Envelope_CarriesTheVerdict(string verdict)
    {
        var e = Env(verdict, "because", "bernex-proxmox", "systemctl status nats");
        Assert.Equal(verdict, (string?)e["verdict"]);
        Assert.Equal("q2", (string?)e["layer"]);
        Assert.Equal("sshgw.execute-command", (string?)e["tool"]);
        Assert.Equal("bernex-proxmox", (string?)e["server"]);
        Assert.Equal("because", (string?)e["reason"]);
    }

    [Fact]
    public void Envelope_ExtractsTheVerb_Basenamed()
    {
        Assert.Equal("systemctl", (string?)Env("allow", "", "s", "systemctl restart x")["verb"]);
        Assert.Equal("reboot", (string?)Env("requiresApproval", "", "s", "/usr/sbin/reboot")["verb"]);
        Assert.Equal("journalctl", (string?)Env("allow", "", "s", "  journalctl -u foo")["verb"]);
    }

    [Fact]
    public void Envelope_TruncatesLongCommands()
    {
        var big = "grep " + new string('x', 500) + " /var/log/app.log";
        var cmd = (string?)Env("allow", "", "s", big)["command"];
        Assert.NotNull(cmd);
        Assert.True(cmd!.Length <= 241, $"expected truncated, got {cmd.Length}");
        Assert.EndsWith("…", cmd);
    }

    [Fact]
    public void Envelope_ScrubsSecretsInTheCommand()
    {
        // A leaked credential-shaped token in the command must not survive into the envelope.
        var e = Env("deny", "secret path", "s", "echo password=hunter2-supersecret");
        var cmd = (string?)e["command"];
        Assert.DoesNotContain("hunter2-supersecret", cmd);
    }

    [Fact]
    public void Envelope_GeneratesCorrelationId_WhenNoneGiven()
    {
        var id = (string?)Env("allow", "", "s", "uptime")["correlation_id"];
        Assert.True(Guid.TryParse(id, out _), "expected a UUID correlation_id");
    }

    [Fact]
    public void Envelope_PreservesGivenCorrelationId()
    {
        var e = Env("allow", "", "s", "uptime", corr: "req-abc-123");
        Assert.Equal("req-abc-123", (string?)e["correlation_id"]);
    }

    [Fact]
    public void FromEnvironmentOrNull_IsNull_WhenUnconfigured()
    {
        // No SSHGW_AUTHZ_NATS_* set in the test env ⇒ emission is a no-op (null publisher),
        // so the MCP runs unchanged where the identity has not been provisioned.
        var saved = (Environment.GetEnvironmentVariable("SSHGW_AUTHZ_NATS_SEED_PATH"),
                     Environment.GetEnvironmentVariable("SSHGW_AUTHZ_NATS_NKEY"));
        Environment.SetEnvironmentVariable("SSHGW_AUTHZ_NATS_SEED_PATH", null);
        Environment.SetEnvironmentVariable("SSHGW_AUTHZ_NATS_NKEY", null);
        try
        {
            Assert.Null(AuthzDecisionPublisher.FromEnvironmentOrNull());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SSHGW_AUTHZ_NATS_SEED_PATH", saved.Item1);
            Environment.SetEnvironmentVariable("SSHGW_AUTHZ_NATS_NKEY", saved.Item2);
        }
    }
}
