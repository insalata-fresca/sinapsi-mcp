using Sinapsi.Nats;
using Xunit;

namespace Sinapsi.Nats.Tests;

// Fail-closed configuration matrix. FromEnvironment() keeps NEUTRAL defaults so a
// no-env local plaintext bus still works, but a *malformed* explicit value is
// rejected at bind time with an error that NAMES the offending env var — rather
// than silently connecting somewhere unintended or hanging forever. This is the
// "throws naming the var, not a silent default" contract.
[Collection("env")]
public sealed class NatsConfigFailClosedTests : IDisposable
{
    private static readonly string[] Vars =
    {
        "NATS_URL", "NATS_NKEY_SEED_PATH", "NATS_NKEY",
        "NATS_TLS_CA_FILE", "NATS_TLS_DISABLE", "NATS_CLIENT_NAME",
        "NATS_CONNECT_TIMEOUT_MS",
    };

    public NatsConfigFailClosedTests() => ClearAll();
    public void Dispose() => ClearAll();
    private static void ClearAll() { foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null); }

    // ---- URL fail-closed -----------------------------------------------------

    [Theory]
    [InlineData("not-a-url")]          // no scheme
    [InlineData("http://host:4222")]   // wrong scheme
    [InlineData("nats://ho st:4222")]  // whitespace
    [InlineData("nats://host\t:4222")] // control char
    public void MalformedUrl_IsRejected_NamingTheVar(string bad)
    {
        Environment.SetEnvironmentVariable("NATS_URL", bad);

        var ex = Assert.Throws<InvalidOperationException>(() => NatsConnectionOptions.FromEnvironment());
        Assert.Contains("NATS_URL", ex.Message);
    }

    [Fact]
    public void UrlWithControlNul_IsRejected()
    {
        // \0 is the C# NUL escape — never a literal NUL byte in the file. Setting a
        // NUL into a process env var truncates on some platforms, so drive the
        // resolved value straight through the record instead of via the env plumbing.
        var o = new NatsConnectionOptions { Url = "nats://host\0:4222" };
        Assert.Throws<InvalidOperationException>(() => o.Validate());
    }

    [Theory]
    [InlineData("nats://127.0.0.1:4222")]
    [InlineData("tls://bus.example.com:4222")]
    [InlineData("ws://bus.example.com:8080")]
    [InlineData("wss://bus.example.com:8443")]
    public void ValidUrlSchemes_AreAccepted(string good)
    {
        Environment.SetEnvironmentVariable("NATS_URL", good);
        Assert.Equal(good, NatsConnectionOptions.FromEnvironment().Url);
    }

    [Fact]
    public void EmptyUrl_FallsBackToNeutralDefault_NotAnError()
    {
        Environment.SetEnvironmentVariable("NATS_URL", "");
        Assert.Equal("nats://127.0.0.1:4222", NatsConnectionOptions.FromEnvironment().Url);
    }

    // ---- NKey fail-closed ----------------------------------------------------

    [Fact]
    public void MalformedNKey_IsRejected_NamingTheVar()
    {
        Environment.SetEnvironmentVariable("NATS_NKEY", "U ABC DEF");   // embedded whitespace
        var ex = Assert.Throws<InvalidOperationException>(() => NatsConnectionOptions.FromEnvironment());
        Assert.Contains("NATS_NKEY", ex.Message);
    }

    [Fact]
    public void UnsetNKey_IsAllowed_NkeyAuthIsOptIn()
    {
        Assert.Null(NatsConnectionOptions.FromEnvironment().NKeyPublic);
    }

    // ---- connect-timeout clamp (fail-closed) ---------------------------------

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-30000")]
    [InlineData("abc")]
    [InlineData("12.5")]
    public void NonPositiveOrNonNumericTimeout_IsRejected_NamingTheVar(string bad)
    {
        Environment.SetEnvironmentVariable("NATS_CONNECT_TIMEOUT_MS", bad);

        var ex = Assert.Throws<InvalidOperationException>(() => NatsConnectionOptions.FromEnvironment());
        Assert.Contains("NATS_CONNECT_TIMEOUT_MS", ex.Message);
    }

    [Fact]
    public void AbsurdlyLargeTimeout_IsRejected()
    {
        Environment.SetEnvironmentVariable(
            "NATS_CONNECT_TIMEOUT_MS",
            (NatsConnectionOptions.MaxConnectTimeoutMs + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var ex = Assert.Throws<InvalidOperationException>(() => NatsConnectionOptions.FromEnvironment());
        Assert.Contains("NATS_CONNECT_TIMEOUT_MS", ex.Message);
    }

    [Fact]
    public void UnsetTimeout_UsesNeutralDefault()
    {
        Assert.Equal(NatsConnectionOptions.DefaultConnectTimeoutMs,
            NatsConnectionOptions.FromEnvironment().ConnectTimeoutMs);
    }

    [Fact]
    public void ValidTimeout_IsBound_AndFlowsIntoNatsOpts()
    {
        Environment.SetEnvironmentVariable("NATS_CONNECT_TIMEOUT_MS", "3500");
        var o = NatsConnectionOptions.FromEnvironment();
        Assert.Equal(3500, o.ConnectTimeoutMs);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), o.BuildNatsOpts().ConnectTimeout);
    }

    // ---- Validate() fail-closes even when constructed directly ---------------

    [Fact]
    public void BuildNatsOpts_OnDirectlyConstructedBadUrl_Throws()
    {
        var o = new NatsConnectionOptions { Url = "not-a-url" };
        Assert.Throws<InvalidOperationException>(() => o.BuildNatsOpts());
    }

    [Fact]
    public void Validate_OnBadTimeout_Throws()
    {
        var o = new NatsConnectionOptions { ConnectTimeoutMs = 0 };
        Assert.Throws<InvalidOperationException>(() => o.Validate());
    }

    [Fact]
    public void Validate_OnGoodDefaults_DoesNotThrow()
    {
        var ex = Record.Exception(() => new NatsConnectionOptions().Validate());
        Assert.Null(ex);
    }
}
