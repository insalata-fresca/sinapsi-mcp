using System.Reflection;
using System.Text.Json.Nodes;
using Sinapsi.Nats;
using Xunit;

namespace Sinapsi.Nats.Tests;

// Input-validation matrix for the public seams. NatsValidation is internal, so the
// unit-level checks drive it by reflection; the public-surface checks assert that
// ConnectAsync(source) and PublishAsync(subject) reject malformed input with an
// ArgumentException BEFORE any network I/O (so no live bus is needed).
public sealed class NatsInputValidationTests
{
    private static readonly Type V =
        typeof(NatsConnectionOptions).Assembly.GetType("Sinapsi.Nats.NatsValidation")!;

    private static string? Call(string method, object? arg) =>
        (string?)V.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new[] { arg });

    // ---- subject -------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has\ttab")]
    [InlineData("has\0nul")]        // \0 is the C# NUL escape — never a literal NUL byte
    [InlineData(".leading")]
    [InlineData("trailing.")]
    [InlineData("double..dot")]
    public void ValidateSubject_RejectsMalformed(string? subject) =>
        Assert.NotNull(Call("ValidateSubject", subject));

    [Theory]
    [InlineData("events.thing.created")]
    [InlineData("a")]
    [InlineData("ns.sub_topic.v1")]
    public void ValidateSubject_AcceptsWellFormed(string subject) =>
        Assert.Null(Call("ValidateSubject", subject));

    [Fact]
    public void ValidateSubject_RejectsOverLong()
    {
        var big = new string('a', 513);
        Assert.NotNull(Call("ValidateSubject", big));
    }

    // ---- source --------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has\0nul")]
    [InlineData("has\nnewline")]
    public void ValidateSource_RejectsMalformed(string? source) =>
        Assert.NotNull(Call("ValidateSource", source));

    [Fact]
    public void ValidateSource_AcceptsUri() =>
        Assert.Null(Call("ValidateSource", "my-service://node-1/"));

    // ---- url -----------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-scheme")]
    [InlineData("http://x")]
    [InlineData("nats://a b")]
    [InlineData("nats://a\0b")]
    public void ValidateUrl_RejectsMalformed(string? url) =>
        Assert.NotNull(Call("ValidateUrl", url));

    [Fact]
    public void ValidateUrl_AcceptsNatsScheme() =>
        Assert.Null(Call("ValidateUrl", "nats://127.0.0.1:4222"));

    // ---- public NKey ---------------------------------------------------------

    [Theory]
    [InlineData("U ABC")]
    [InlineData("U\tABC")]
    [InlineData("U\0ABC")]
    public void ValidateNKeyPublic_RejectsMalformedNonEmpty(string nkey) =>
        Assert.NotNull(Call("ValidateNKeyPublic", nkey));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateNKeyPublic_AllowsUnset(string? nkey) =>
        Assert.Null(Call("ValidateNKeyPublic", nkey));

    // ---- public-surface: ConnectAsync(source) rejects before I/O -------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ConnectAsync_MalformedSource_ThrowsArgumentException_NoIo(string? source)
    {
        var opts = new NatsConnectionOptions { TlsDisable = true };
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await NatsEventPublisher.ConnectAsync(opts, source!));
    }

    [Fact]
    public async Task ConnectAsync_NullOpts_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await NatsEventPublisher.ConnectAsync(null!, "svc://n/"));
    }
}
