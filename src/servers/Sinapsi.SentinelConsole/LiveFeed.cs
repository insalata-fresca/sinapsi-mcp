using System.Threading.Channels;

namespace Sinapsi.SentinelConsole;

/// <summary>
/// In-process fan-out of newly-recorded decisions to connected Server-Sent-Events
/// clients (the live feed in the Console). Each browser gets its own bounded channel;
/// a slow client drops oldest rather than back-pressuring the ingest path. Publishing
/// is lock-free-ish (a short lock over the subscriber set) and never blocks the bus
/// subscriber.
/// </summary>
public sealed class LiveFeed
{
    private readonly HashSet<Channel<AuthzDecision>> _subs = new();
    private readonly object _lock = new();

    /// <summary>Register a new SSE client. Dispose the returned subscription on disconnect.</summary>
    public Subscription Subscribe()
    {
        var ch = Channel.CreateBounded<AuthzDecision>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock) _subs.Add(ch);
        return new Subscription(this, ch);
    }

    public void Publish(AuthzDecision d)
    {
        lock (_lock)
            foreach (var ch in _subs)
                ch.Writer.TryWrite(d);   // bounded + drop-oldest ⇒ never blocks
    }

    private void Remove(Channel<AuthzDecision> ch)
    {
        lock (_lock) _subs.Remove(ch);
        ch.Writer.TryComplete();
    }

    public int ClientCount { get { lock (_lock) return _subs.Count; } }

    public sealed class Subscription : IDisposable
    {
        private readonly LiveFeed _feed;
        private readonly Channel<AuthzDecision> _ch;
        internal Subscription(LiveFeed feed, Channel<AuthzDecision> ch) { _feed = feed; _ch = ch; }
        public IAsyncEnumerable<AuthzDecision> ReadAllAsync(CancellationToken ct)
            => _ch.Reader.ReadAllAsync(ct);
        public void Dispose() => _feed.Remove(_ch);
    }
}
