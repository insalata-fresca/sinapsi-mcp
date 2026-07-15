using System.Text.Json.Nodes;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>Published-Language conformance checker (docs/64 §3 / docs/61 §8). Proves the shared
/// contract accepts a well-formed envelope from ANY layer and rejects vocabulary/shape breaks.</summary>
public sealed class DecisionEnvelopeContractTests
{
    // A minimal Q1-style envelope data payload built independently (as the gateway PEP does).
    private static JsonObject Q1(string verdict = "allow") => new()
    {
        ["layer"] = "q1", ["question"] = "identity-tool", ["surface"] = "gateway-pep",
        ["tool"] = "sshgw.execute-command", ["server"] = "", ["verb"] = "",
        ["verdict"] = verdict, ["reason"] = "granted", ["command"] = "", ["correlation_id"] = "",
        ["agent"] = "agent:cervello-watcher", // extra field — Published Language permits it
    };

    [Fact]
    public void ConformantEnvelope_HasNoErrors()
    {
        Assert.True(DecisionEnvelopeContract.IsConformant(Q1(), out var errors), string.Join("; ", errors));
    }

    [Fact]
    public void ExtraFields_AreAllowed_NotASharedKernel()
    {
        var e = Q1();
        e["custom_layer_specific"] = "anything";
        Assert.Empty(DecisionEnvelopeContract.Validate(e));
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("requiresApproval")]
    [InlineData("deny")]
    public void AllSharedVerdicts_AreAccepted(string verdict)
        => Assert.Empty(DecisionEnvelopeContract.Validate(Q1(verdict)));

    [Fact]
    public void UnknownVerdict_IsRejected()
    {
        var errors = DecisionEnvelopeContract.Validate(Q1("maybe"));
        Assert.Contains(errors, e => e.Contains("verdict") && e.Contains("maybe"));
    }

    [Fact]
    public void UnknownLayer_IsRejected()
    {
        var e = Q1(); e["layer"] = "q9";
        Assert.Contains(DecisionEnvelopeContract.Validate(e), s => s.Contains("layer"));
    }

    [Fact]
    public void MissingRequiredField_IsRejected()
    {
        var e = Q1(); e.Remove("correlation_id");
        Assert.Contains(DecisionEnvelopeContract.Validate(e), s => s.Contains("correlation_id"));
    }

    [Fact]
    public void NonStringField_IsRejected()
    {
        var e = Q1(); e["reason"] = 42;
        Assert.Contains(DecisionEnvelopeContract.Validate(e), s => s.Contains("reason") && s.Contains("string"));
    }

    [Fact]
    public void NullData_IsRejected()
        => Assert.NotEmpty(DecisionEnvelopeContract.Validate(null));

    [Fact]
    public void CanonicalSchemaPath_PointsAtTheHomeServerPap()
    {
        Assert.Equal("policies/authz/decision-envelope.v1.schema.json", DecisionEnvelopeContract.CanonicalSchemaPath);
        Assert.Equal("1", DecisionEnvelopeContract.SchemaVersion);
    }
}
