using System.Text.Json.Nodes;
using Sinapsi.Nats;

namespace ConfigSpine.Mcp;

/// <summary>
/// Production <see cref="IConfigEventSink"/>: wraps the in-repo <see cref="NatsEventPublisher"/>
/// (Sinapsi.Nats) so a published config event carries the canonical CloudEvents v1.0 envelope over
/// an NKey + pinned-CA TLS connection — the same shape and transport as the reference
/// <c>emit_config_event.py</c> emitter, reused rather than re-implemented.
///
/// <para>
/// The connection is opened LAZILY on first publish and then held open for reuse. This keeps the
/// MCP backend bootable when NATS is briefly unreachable (it fails the individual call with a
/// sanitized error instead of refusing to start); on a publish failure the publisher is reset so
/// the next call reconnects.
/// </para>
/// </summary>
internal sealed class NatsConfigEventSink(NatsConnectionOptions opts, string source)
    : IConfigEventSink, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NatsEventPublisher? _publisher;

    public async Task PublishAsync(string subject, JsonObject data, CancellationToken ct)
    {
        var publisher = await GetPublisherAsync(ct).ConfigureAwait(false);
        try
        {
            // Set the CloudEvents `subject` attribute to the same value (mirrors the reference emitter).
            await publisher.PublishAsync(subject, data, subjectAttr: subject, ct).ConfigureAwait(false);
        }
        catch
        {
            // Drop the (possibly wedged) connection so the next call reconnects cleanly.
            await ResetAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<NatsEventPublisher> GetPublisherAsync(CancellationToken ct)
    {
        if (_publisher is not null)
            return _publisher;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _publisher ??= await NatsEventPublisher.ConnectAsync(opts, source, ct).ConfigureAwait(false);
            return _publisher;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_publisher is not null)
            {
                await _publisher.DisposeAsync().ConfigureAwait(false);
                _publisher = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_publisher is not null)
            await _publisher.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
