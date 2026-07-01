using System.Text.Json;
using System.Text.Json.Nodes;
using NATS.Client.Core;

namespace Sinapsi.Nats;

/// <summary>
/// Publishes events as <see href="https://cloudevents.io">CloudEvents v1.0</see>
/// envelopes, so every consumer parses them identically. The connection
/// (NKey+TLS) is held open for the lifetime of the publisher.
/// </summary>
/// <remarks>
/// The CloudEvents <c>type</c> attribute is <c>{TypePrefix}{subject}</c>, where the
/// prefix is a reverse-DNS namespace read from the <c>CLOUDEVENTS_TYPE_PREFIX</c> env
/// var (default <see cref="DefaultTypePrefix"/>). Set it to your own domain so emitted
/// events carry a namespace you own.
/// </remarks>
public sealed class NatsEventPublisher : IAsyncDisposable
{
    /// <summary>Neutral default reverse-DNS prefix for the CloudEvents <c>type</c> attribute.
    /// Override with the <c>CLOUDEVENTS_TYPE_PREFIX</c> env var.</summary>
    public const string DefaultTypePrefix = "com.example.";

    private static string ResolveTypePrefix() =>
        Environment.GetEnvironmentVariable("CLOUDEVENTS_TYPE_PREFIX") is { Length: > 0 } p ? p : DefaultTypePrefix;

    private readonly NatsConnection _nc;
    private readonly string _source;
    private readonly string _typePrefix;

    private NatsEventPublisher(NatsConnection nc, string source, string typePrefix)
    {
        _nc = nc;
        _source = source;
        _typePrefix = typePrefix;
    }

    /// <summary>Connect (NKey+TLS) and return a ready publisher. <paramref name="source"/>
    /// is the CloudEvents producer URI, e.g. <c>my-service://node-1/</c>. The CloudEvents
    /// <c>type</c> prefix is taken from the <c>CLOUDEVENTS_TYPE_PREFIX</c> env var
    /// (default <see cref="DefaultTypePrefix"/>).</summary>
    public static async Task<NatsEventPublisher> ConnectAsync(NatsConnectionOptions opts, string source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (NatsValidation.ValidateSource(source) is { } reason)
            throw new ArgumentException(reason, nameof(source));

        // opts.BuildNatsOpts() fail-closes on malformed URL / NKey / timeout config.
        var nc = new NatsConnection(opts.BuildNatsOpts());
        try
        {
            await nc.ConnectAsync();
        }
        catch (Exception e)
        {
            // A NATS connect failure can carry the URL (with embedded credentials) or
            // a mis-wired seed — never surface it raw. Sanitize before it escapes.
            await nc.DisposeAsync();
            throw NatsErrors.Wrap("NATS connect failed", e);
        }
        return new NatsEventPublisher(nc, source, ResolveTypePrefix());
    }

    /// <summary>Publish <paramref name="data"/> on the NATS <paramref name="subject"/> wrapped in a CloudEvent.
    /// The <paramref name="subject"/> is validated (required, length-capped, control-char / empty-token free)
    /// before publish; a malformed subject throws <see cref="ArgumentException"/>. A publish failure is
    /// re-thrown through <see cref="NatsErrors"/> so no seed/NKey/credential leaks into the message.</summary>
    public async ValueTask PublishAsync(string subject, JsonObject data, string? subjectAttr = null, CancellationToken ct = default)
    {
        if (NatsValidation.ValidateSubject(subject) is { } reason)
            throw new ArgumentException(reason, nameof(subject));
        ArgumentNullException.ThrowIfNull(data);

        var env = new JsonObject
        {
            ["specversion"] = "1.0",
            ["type"] = _typePrefix + subject,
            ["source"] = _source,
            // "D" = canonical dashed UUID (8-4-4-4-12), the CloudEvents UUIDv4 shape.
            ["id"] = Guid.NewGuid().ToString("D"),
            ["time"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            ["datacontenttype"] = "application/json",
            ["data"] = data,
        };
        if (subjectAttr is not null)
            env["subject"] = subjectAttr;

        var payload = JsonSerializer.SerializeToUtf8Bytes(env);
        try
        {
            await _nc.PublishAsync(subject, payload, cancellationToken: ct);
        }
        catch (Exception e) when (e is not ArgumentException and not OperationCanceledException)
        {
            throw NatsErrors.Wrap("NATS publish failed", e);
        }
    }

    public async ValueTask DisposeAsync() => await _nc.DisposeAsync();
}
