using System.Text.Json.Nodes;
using Sinapsi.Nats;

namespace Sinapsi.Indexer;

/// <summary>
/// Lazily-connected NATS publisher for learning-published events — the write half
/// of the indexer (the canonical ingress for "publish a learning"). Holds one
/// NKey+TLS connection open after first use. A downstream materializer consumes
/// the event and writes the learnings repo; this indexer then re-scans + serves it.
///
/// The subject is <c>{LEARN_SUBJECT_PREFIX}.{scope}.published</c> (env-driven,
/// neutral default <c>events.learn</c>), and the CloudEvents producer URI is the
/// env-driven <c>LEARN_EVENT_SOURCE</c>.
/// </summary>
public sealed class LearnPublisher : IAsyncDisposable
{
    private readonly NatsConnectionOptions _opts;
    private readonly string _subjectPrefix;
    private readonly string _eventSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NatsEventPublisher? _pub;

    public LearnPublisher(NatsConnectionOptions opts)
    {
        // Use a SCOPED publish-only learn identity (publish allow on the learn
        // subjects) if provided — NOT a broad admin nkey. Falls back to the
        // injected opts only when LEARN_NATS_* is unset, so the service still starts.
        var seedPath = Environment.GetEnvironmentVariable("LEARN_NATS_SEED_PATH");
        var nkey = Environment.GetEnvironmentVariable("LEARN_NATS_NKEY");
        _opts = opts with
        {
            ClientName = "sinapsi-indexer-learn",
            NKeySeedPath = string.IsNullOrWhiteSpace(seedPath) ? opts.NKeySeedPath : seedPath,
            NKeyPublic = string.IsNullOrWhiteSpace(nkey) ? opts.NKeyPublic : nkey,
        };
        _subjectPrefix = Env("LEARN_SUBJECT_PREFIX", "events.learn");
        _eventSource = Env("LEARN_EVENT_SOURCE", "sinapsi-indexer://local/");
    }

    private static string Env(string k, string dflt) =>
        Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : dflt;

    /// <summary>The NATS subject a publish for the given scope lands on.</summary>
    public string SubjectFor(string scope) => $"{_subjectPrefix}.{scope}.published";

    private async ValueTask<NatsEventPublisher> GetAsync(CancellationToken ct)
    {
        if (_pub is not null) return _pub;
        await _gate.WaitAsync(ct);
        try
        {
            _pub ??= await NatsEventPublisher.ConnectAsync(_opts, _eventSource, ct);
        }
        finally
        {
            _gate.Release();
        }
        return _pub;
    }

    public async Task PublishLearningAsync(string slug, string scope, JsonObject data, CancellationToken ct)
    {
        var pub = await GetAsync(ct);
        await pub.PublishAsync(SubjectFor(scope), data, subjectAttr: slug, ct: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pub is not null) await _pub.DisposeAsync();
    }
}
