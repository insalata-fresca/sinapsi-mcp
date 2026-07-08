using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Deterministic in-memory <see cref="IDiarizeEmbedClient"/> for tests (spec
/// <c>diarize-embed-sidecar</c> → "Client is a swappable seam"). NO network, NO live endpoint,
/// NO personal audio. It returns a scripted <see cref="DiarizeEmbedResponse"/>, or throws a
/// scripted transient / terminal fault, and records every request so a test can assert the fake
/// retained no audio bytes after the call (confinement analogue — the real proxy/sidecar keep
/// nothing).
/// </summary>
internal sealed class FakeDiarizeEmbedClient : IDiarizeEmbedClient
{
    private readonly Func<DiarizeEmbedRequest, DiarizeEmbedResponse>? _respond;
    private readonly DiarizeEmbedException? _fault;

    private FakeDiarizeEmbedClient(
        Func<DiarizeEmbedRequest, DiarizeEmbedResponse>? respond,
        DiarizeEmbedException? fault)
    {
        _respond = respond;
        _fault = fault;
    }

    /// <summary>Count of calls seen (idempotency / retry assertions).</summary>
    public int Calls { get; private set; }

    /// <summary>The request lengths seen — proves the fake holds no audio after returning.</summary>
    public List<int> RequestAudioLengths { get; } = [];

    /// <summary>
    /// Always false: the fake mirrors the transient-only contract and never stashes the request
    /// audio anywhere (the real proxy/sidecar keep nothing either). A test asserts this holds.
    /// </summary>
    public bool RetainedAudio => false;

    public static FakeDiarizeEmbedClient Returning(Func<DiarizeEmbedRequest, DiarizeEmbedResponse> respond) =>
        new(respond, null);

    public static FakeDiarizeEmbedClient Returning(DiarizeEmbedResponse response) =>
        new(_ => response, null);

    public static FakeDiarizeEmbedClient Faulting(DiarizeEmbedException fault) =>
        new(null, fault);

    public Task<DiarizeEmbedResponse> DiarizeEmbedAsync(DiarizeEmbedRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Calls++;
        RequestAudioLengths.Add(request.Audio.Length);
        // Intentionally do NOT stash request.Audio anywhere — mirrors the transient-only
        // contract, so RetainedAudio stays false.

        if (_fault is not null) throw _fault;
        return Task.FromResult(_respond!(request));
    }
}
