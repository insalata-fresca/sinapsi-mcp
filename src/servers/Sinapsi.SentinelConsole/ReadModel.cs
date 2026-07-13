namespace Sinapsi.SentinelConsole;

/// <summary>
/// The in-memory read-model behind the Sentinel Console — the missing projection that
/// makes the authorization posture visible without an agent session (home-server
/// <c>docs/61 §7</c>). It ingests normalized <see cref="AuthzDecision"/>s from the bus
/// and serves three views:
///   • <see cref="Posture"/>  — the at-a-glance grid: per tool × layer, the latest verdict
///                              + running allow/approval/deny counts + last-seen;
///   • <see cref="Recent"/>   — the live decision feed (newest first);
///   • <see cref="Chain"/>    — every decision sharing a correlation id (one request's path
///                              through Q1/Q2/Q3), once a shared trace id is threaded.
///
/// Bounded + thread-safe: the feed is a fixed-capacity ring (oldest evicted), so memory is
/// capped regardless of traffic. Aggregates are cheap counters, never unbounded.
/// </summary>
public sealed class ReadModel
{
    private readonly int _capacity;
    private readonly LinkedList<AuthzDecision> _recent = new();   // newest at head
    private readonly Dictionary<(string Tool, string Layer), PostureCell> _posture = new();
    private readonly object _lock = new();
    private long _total;

    public ReadModel(int capacity = 2000) => _capacity = Math.Max(1, capacity);

    public long Total { get { lock (_lock) return _total; } }

    public void Record(AuthzDecision d)
    {
        lock (_lock)
        {
            _total++;
            _recent.AddFirst(d);
            while (_recent.Count > _capacity) _recent.RemoveLast();

            var key = (d.Tool, d.Layer);
            if (!_posture.TryGetValue(key, out var cell))
                cell = _posture[key] = new PostureCell();
            cell.Apply(d);
        }
    }

    /// <summary>The posture grid, one row per (tool, layer), tool-then-layer ordered.</summary>
    public IReadOnlyList<PostureRow> Posture()
    {
        lock (_lock)
        {
            return _posture
                .Select(kv => new PostureRow(
                    kv.Key.Tool, kv.Key.Layer,
                    kv.Value.LastVerdict, kv.Value.LastSeen,
                    kv.Value.Allow, kv.Value.Approval, kv.Value.Deny))
                .OrderBy(r => r.Tool, StringComparer.Ordinal)
                .ThenBy(r => r.Layer, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>The most recent <paramref name="n"/> decisions, newest first.</summary>
    public IReadOnlyList<AuthzDecision> Recent(int n)
    {
        lock (_lock)
            return _recent.Take(Math.Max(0, n)).ToList();
    }

    /// <summary>All buffered decisions sharing a correlation id, oldest→newest (the
    /// per-request chain). Empty when the id is unknown or blank.</summary>
    public IReadOnlyList<AuthzDecision> Chain(string correlationId)
    {
        if (string.IsNullOrEmpty(correlationId)) return Array.Empty<AuthzDecision>();
        lock (_lock)
            return _recent
                .Where(d => d.CorrelationId == correlationId)
                .OrderBy(d => d.Time)
                .ToList();
    }

    private sealed class PostureCell
    {
        public long Allow, Approval, Deny;
        public string LastVerdict = "";
        public DateTimeOffset LastSeen;

        public void Apply(AuthzDecision d)
        {
            switch (d.Verdict)
            {
                case AuthzDecision.VerdictAllow: Allow++; break;
                case AuthzDecision.VerdictApproval: Approval++; break;
                case AuthzDecision.VerdictDeny: Deny++; break;
            }
            LastVerdict = d.Verdict;
            if (d.Time >= LastSeen) LastSeen = d.Time;
        }
    }
}

/// <summary>One cell of the posture grid.</summary>
public sealed record PostureRow(
    string Tool, string Layer, string LastVerdict, DateTimeOffset LastSeen,
    long Allow, long Approval, long Deny);
