using System.Reflection;
using Sinapsi.Nats;
using Xunit;

namespace Sinapsi.Nats.Tests;

/// <summary>
/// Tests for <see cref="NatsEventPublisher"/> that don't need a live bus:
/// the public default type-prefix constant and the env-driven prefix resolution
/// (exercised through the private resolver to keep the assert meaningful without
/// opening a connection).
/// </summary>
[Collection("env")]
public sealed class NatsEventPublisherTests : IDisposable
{
    public NatsEventPublisherTests() => Environment.SetEnvironmentVariable("CLOUDEVENTS_TYPE_PREFIX", null);
    public void Dispose() => Environment.SetEnvironmentVariable("CLOUDEVENTS_TYPE_PREFIX", null);

    private static string ResolvePrefix() =>
        (string)typeof(NatsEventPublisher)
            .GetMethod("ResolveTypePrefix", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

    [Fact]
    public void DefaultTypePrefix_IsNeutralReverseDns()
    {
        Assert.Equal("com.example.", NatsEventPublisher.DefaultTypePrefix);
        // No environment-specific brand/domain baked into the default.
        Assert.DoesNotContain("insalata", NatsEventPublisher.DefaultTypePrefix);
    }

    [Fact]
    public void TypePrefix_DefaultsToConstant_WhenEnvUnset()
    {
        Assert.Equal(NatsEventPublisher.DefaultTypePrefix, ResolvePrefix());
    }

    [Fact]
    public void TypePrefix_HonoursEnvOverride()
    {
        Environment.SetEnvironmentVariable("CLOUDEVENTS_TYPE_PREFIX", "io.acme.");
        Assert.Equal("io.acme.", ResolvePrefix());
    }

    [Fact]
    public void TypePrefix_EmptyEnv_FallsBackToDefault()
    {
        Environment.SetEnvironmentVariable("CLOUDEVENTS_TYPE_PREFIX", "");
        Assert.Equal(NatsEventPublisher.DefaultTypePrefix, ResolvePrefix());
    }

    [Fact]
    public void NatsEventPublisher_IsAsyncDisposable()
    {
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(NatsEventPublisher)));
    }
}
