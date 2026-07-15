using Xunit;

namespace ApprovalBridge.Mcp.Tests;

/// <summary>Fail-closed env-driven config. Every test runs in its own isolated env-var snapshot
/// (collection-serial via <see cref="EnvCollection"/>) so parallel test runs never race on
/// process-wide environment variables.</summary>
[Collection("ApprovalBridgeEnv")]
public sealed class ApprovalBridgeOptionsTests : IDisposable
{
    private static readonly string[] Vars =
    {
        "APPROVAL_BRIDGE_BROKER_URL",
        "APPROVAL_BRIDGE_REQUESTER_IDENTITY",
        "APPROVAL_BRIDGE_HTTP_TIMEOUT_MS",
    };

    private readonly Dictionary<string, string?> _saved = new();

    public ApprovalBridgeOptionsTests()
    {
        foreach (var v in Vars) _saved[v] = Environment.GetEnvironmentVariable(v);
    }

    public void Dispose()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, _saved[v]);
    }

    private static void Clear()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
    }

    [Fact]
    public void FromEnvironment_ThrowsWhenBrokerUrlMissing()
    {
        Clear();
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_REQUESTER_IDENTITY", "agent:x/y");
        var ex = Assert.Throws<InvalidOperationException>(() => ApprovalBridgeOptions.FromEnvironment());
        Assert.Contains("APPROVAL_BRIDGE_BROKER_URL", ex.Message);
    }

    [Fact]
    public void FromEnvironment_ThrowsWhenBrokerUrlIsNotAnAbsoluteHttpUrl()
    {
        Clear();
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_BROKER_URL", "not-a-url");
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_REQUESTER_IDENTITY", "agent:x/y");
        var ex = Assert.Throws<InvalidOperationException>(() => ApprovalBridgeOptions.FromEnvironment());
        Assert.Contains("APPROVAL_BRIDGE_BROKER_URL", ex.Message);
    }

    [Fact]
    public void FromEnvironment_ThrowsWhenRequesterIdentityMissing()
    {
        Clear();
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_BROKER_URL", "http://broker:8013");
        var ex = Assert.Throws<InvalidOperationException>(() => ApprovalBridgeOptions.FromEnvironment());
        Assert.Contains("APPROVAL_BRIDGE_REQUESTER_IDENTITY", ex.Message);
    }

    [Fact]
    public void FromEnvironment_TrimsTrailingSlashFromBrokerUrl()
    {
        Clear();
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_BROKER_URL", "http://broker:8013/");
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_REQUESTER_IDENTITY", "agent:x/y");
        var opt = ApprovalBridgeOptions.FromEnvironment();
        Assert.Equal("http://broker:8013", opt.BrokerBaseUrl);
    }

    [Fact]
    public void FromEnvironment_DefaultsHttpTimeout()
    {
        Clear();
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_BROKER_URL", "http://broker:8013");
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_REQUESTER_IDENTITY", "agent:x/y");
        var opt = ApprovalBridgeOptions.FromEnvironment();
        Assert.Equal(ApprovalBridgeOptions.DefaultHttpTimeoutMs, opt.HttpTimeoutMs);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("999999999")]
    public void FromEnvironment_ThrowsOnInvalidHttpTimeout(string raw)
    {
        Clear();
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_BROKER_URL", "http://broker:8013");
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_REQUESTER_IDENTITY", "agent:x/y");
        Environment.SetEnvironmentVariable("APPROVAL_BRIDGE_HTTP_TIMEOUT_MS", raw);
        var ex = Assert.Throws<InvalidOperationException>(() => ApprovalBridgeOptions.FromEnvironment());
        Assert.Contains("APPROVAL_BRIDGE_HTTP_TIMEOUT_MS", ex.Message);
    }
}

/// <summary>Serialises every test that mutates process-wide environment variables so parallel
/// xUnit test runs don't race on the same env-var keys.</summary>
[CollectionDefinition("ApprovalBridgeEnv", DisableParallelization = true)]
public sealed class EnvCollection { }
