using NATS.Client.Core;
using Sinapsi.Nats;
using Xunit;

namespace Sinapsi.Nats.Tests;

/// <summary>
/// Tests for <see cref="NatsHomelabOptions"/>: the env-var contract and the three
/// TLS branches of <see cref="NatsHomelabOptions.BuildNatsOpts"/>. These mutate
/// process env vars, so the whole class runs in a single (non-parallel) collection.
/// </summary>
[Collection("env")]
public sealed class NatsHomelabOptionsTests : IDisposable
{
    private static readonly string[] Vars =
    {
        "NATS_URL", "NATS_NKEY_SEED_PATH", "NATS_NKEY",
        "NATS_TLS_CA_FILE", "NATS_TLS_DISABLE", "NATS_CLIENT_NAME",
    };

    public NatsHomelabOptionsTests() => ClearAll();
    public void Dispose() => ClearAll();

    private static void ClearAll()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
    }

    [Fact]
    public void Defaults_AreNeutral_NotEnvironmentSpecific()
    {
        var o = new NatsHomelabOptions();

        Assert.Equal("nats://127.0.0.1:4222", o.Url);
        Assert.Equal("nats.seed", o.NKeySeedPath);
        Assert.Null(o.NKeyPublic);
        Assert.Null(o.TlsCaFile);
        Assert.False(o.TlsDisable);
        Assert.Equal("sinapsi-nats", o.ClientName);
    }

    [Fact]
    public void FromEnvironment_WithNothingSet_ReturnsDefaults()
    {
        var o = NatsHomelabOptions.FromEnvironment();

        Assert.Equal("nats://127.0.0.1:4222", o.Url);
        Assert.Equal("nats.seed", o.NKeySeedPath);
        Assert.Null(o.NKeyPublic);
        Assert.Null(o.TlsCaFile);
        Assert.False(o.TlsDisable);
        Assert.Equal("sinapsi-nats", o.ClientName);
    }

    [Fact]
    public void FromEnvironment_BindsEveryVar()
    {
        Environment.SetEnvironmentVariable("NATS_URL", "nats://10.0.0.5:4333");
        Environment.SetEnvironmentVariable("NATS_NKEY_SEED_PATH", "/secrets/x.seed");
        Environment.SetEnvironmentVariable("NATS_NKEY", "UABCDEF");
        Environment.SetEnvironmentVariable("NATS_TLS_CA_FILE", "/secrets/ca.crt");
        Environment.SetEnvironmentVariable("NATS_TLS_DISABLE", "1");
        Environment.SetEnvironmentVariable("NATS_CLIENT_NAME", "my-svc");

        var o = NatsHomelabOptions.FromEnvironment();

        Assert.Equal("nats://10.0.0.5:4333", o.Url);
        Assert.Equal("/secrets/x.seed", o.NKeySeedPath);
        Assert.Equal("UABCDEF", o.NKeyPublic);
        Assert.Equal("/secrets/ca.crt", o.TlsCaFile);
        Assert.True(o.TlsDisable);
        Assert.Equal("my-svc", o.ClientName);
    }

    [Fact]
    public void FromEnvironment_EmptyCaFile_FallsBackToNull()
    {
        Environment.SetEnvironmentVariable("NATS_TLS_CA_FILE", "");
        var o = NatsHomelabOptions.FromEnvironment();
        Assert.Null(o.TlsCaFile);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]   // only 1 / true are truthy
    [InlineData("", false)]
    public void FromEnvironment_TlsDisable_TruthyParsing(string value, bool expected)
    {
        Environment.SetEnvironmentVariable("NATS_TLS_DISABLE", value);
        Assert.Equal(expected, NatsHomelabOptions.FromEnvironment().TlsDisable);
    }

    [Fact]
    public void BuildNatsOpts_CarriesUrlAndClientName()
    {
        var o = new NatsHomelabOptions { Url = "nats://host:9999", ClientName = "abc" };
        var opts = o.BuildNatsOpts();
        Assert.Equal("nats://host:9999", opts.Url);
        Assert.Equal("abc", opts.Name);
    }

    [Fact]
    public void BuildNatsOpts_Default_NoCa_UsesAutoTlsWithoutPinnedCa()
    {
        var o = new NatsHomelabOptions(); // TlsCaFile null, TlsDisable false
        var opts = o.BuildNatsOpts();

        Assert.NotNull(opts.TlsOpts);
        Assert.Equal(TlsMode.Auto, opts.TlsOpts!.Mode);
        Assert.True(string.IsNullOrEmpty(opts.TlsOpts.CaFile));
    }

    [Fact]
    public void BuildNatsOpts_WithCaFile_PinsCaAndUsesAuto()
    {
        var o = new NatsHomelabOptions { TlsCaFile = "/secrets/ca.crt" };
        var opts = o.BuildNatsOpts();

        Assert.NotNull(opts.TlsOpts);
        Assert.Equal(TlsMode.Auto, opts.TlsOpts!.Mode);
        Assert.Equal("/secrets/ca.crt", opts.TlsOpts.CaFile);
    }

    [Fact]
    public void BuildNatsOpts_TlsDisable_DisablesTlsAndDropsCa()
    {
        // Even with a CA configured, TlsDisable must win and drop the CA entirely.
        var o = new NatsHomelabOptions { TlsCaFile = "/secrets/ca.crt", TlsDisable = true };
        var opts = o.BuildNatsOpts();

        Assert.NotNull(opts.TlsOpts);
        Assert.Equal(TlsMode.Disable, opts.TlsOpts!.Mode);
        Assert.True(string.IsNullOrEmpty(opts.TlsOpts.CaFile));
    }

    [Fact]
    public void BuildNatsOpts_NKeyPublic_FlowsIntoAuthOpts()
    {
        var o = new NatsHomelabOptions { NKeyPublic = "UPUBLICKEY" };
        var opts = o.BuildNatsOpts();

        Assert.NotNull(opts.AuthOpts);
        Assert.Equal("UPUBLICKEY", opts.AuthOpts!.NKey);
    }

    [Fact]
    public void BuildNatsOpts_MissingSeedFile_DoesNotThrow_AndLeavesSeedUnset()
    {
        // Neutral default points at a relative "nats.seed" that does not exist in the
        // test working dir: BuildNatsOpts must tolerate that rather than throwing.
        var o = new NatsHomelabOptions { NKeySeedPath = "definitely-absent.seed" };
        var opts = o.BuildNatsOpts();

        Assert.NotNull(opts.AuthOpts);
        Assert.True(string.IsNullOrEmpty(opts.AuthOpts!.Seed));
    }

    [Fact]
    public void BuildNatsOpts_SeedFilePresent_IsReadAndTrimmed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nats-{Guid.NewGuid():N}.seed");
        File.WriteAllText(path, "  SSEEDVALUE  \n");
        try
        {
            var o = new NatsHomelabOptions { NKeySeedPath = path };
            var opts = o.BuildNatsOpts();
            // File content was surrounded by whitespace/newline; BuildNatsOpts trims it.
            Assert.Equal("SSEEDVALUE", opts.AuthOpts!.Seed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Record_WithExpression_OverridesSingleField()
    {
        var o = NatsHomelabOptions.FromEnvironment() with { ClientName = "renamed" };
        Assert.Equal("renamed", o.ClientName);
    }
}
