namespace Sinapsi.SentinelConsole;

/// <summary>
/// The in-memory read-model behind the Console's deploy-visibility lane — mirrors
/// <see cref="ReadModel"/>'s shape for the authz plane, but projects
/// <see cref="DeployEvent"/>s instead: "did my merge actually deploy?" answered from the
/// same screen, no SSH-to-check. It ingests normalized events from the release plane
/// (<c>homelab.release.&lt;svc&gt;.published</c>) and the per-host deploy-controller
/// (<c>homelab.deploy.&lt;ctid&gt;.&lt;svc&gt;.applied|failed</c>) and serves two views:
///   • <see cref="Recent"/> — the live event feed (newest first);
///   • <see cref="State"/>  — per-service latest: last released version/digest + last
///                            applied version/digest/ctid/result.
///
/// Bounded + thread-safe: the feed is a fixed-capacity ring (oldest evicted), so memory is
/// capped regardless of traffic. Per-service state is one cell per distinct <c>svc</c> seen —
/// naturally bounded by the number of homelab services, never unbounded.
/// </summary>
public sealed class DeployModel
{
    private readonly int _capacity;
    private readonly LinkedList<DeployEvent> _recent = new();   // newest at head
    private readonly Dictionary<string, ServiceState> _state = new();   // key = svc
    private readonly object _lock = new();
    private long _total;

    public DeployModel(int capacity = 500) => _capacity = Math.Max(1, capacity);

    public long Total { get { lock (_lock) return _total; } }

    public void Record(DeployEvent e)
    {
        lock (_lock)
        {
            _total++;
            _recent.AddFirst(e);
            while (_recent.Count > _capacity) _recent.RemoveLast();

            if (!_state.TryGetValue(e.Svc, out var s))
                s = _state[e.Svc] = new ServiceState();
            s.Apply(e);
        }
    }

    /// <summary>The most recent <paramref name="n"/> deploy events, newest first.</summary>
    public IReadOnlyList<DeployEvent> Recent(int n)
    {
        lock (_lock)
            return _recent.Take(Math.Max(0, n)).ToList();
    }

    /// <summary>Per-service latest state, one row per distinct service seen, svc-ordered.</summary>
    public IReadOnlyList<DeployServiceRow> State()
    {
        lock (_lock)
        {
            return _state
                .Select(kv => new DeployServiceRow(
                    kv.Key,
                    kv.Value.LastReleasedVersion, kv.Value.LastReleasedDigest, kv.Value.LastReleasedAt,
                    kv.Value.LastAppliedVersion, kv.Value.LastAppliedDigest, kv.Value.LastAppliedCtid,
                    kv.Value.LastAppliedAt, kv.Value.LastResult))
                .OrderBy(r => r.Svc, StringComparer.Ordinal)
                .ToList();
        }
    }

    private sealed class ServiceState
    {
        public string LastReleasedVersion = "";
        public string LastReleasedDigest = "";
        public DateTimeOffset LastReleasedAt;

        public string LastAppliedVersion = "";
        public string LastAppliedDigest = "";
        public string LastAppliedCtid = "";
        public DateTimeOffset LastAppliedAt;
        public string LastResult = "";   // applied | failed — the deploy-controller's latest outcome

        public void Apply(DeployEvent e)
        {
            switch (e.Kind)
            {
                case DeployEvent.KindReleased:
                    if (e.Time >= LastReleasedAt)
                    {
                        LastReleasedVersion = e.Version;
                        LastReleasedDigest = e.Digest;
                        LastReleasedAt = e.Time;
                    }
                    break;
                case DeployEvent.KindApplied:
                case DeployEvent.KindFailed:
                    if (e.Time >= LastAppliedAt)
                    {
                        LastAppliedVersion = e.Version;
                        LastAppliedDigest = e.Digest;
                        LastAppliedCtid = e.Ctid;
                        LastAppliedAt = e.Time;
                        LastResult = e.Kind;
                    }
                    break;
            }
        }
    }
}

/// <summary>One row of the per-service deploy-state view.</summary>
public sealed record DeployServiceRow(
    string Svc,
    string LastReleasedVersion, string LastReleasedDigest, DateTimeOffset LastReleasedAt,
    string LastAppliedVersion, string LastAppliedDigest, string LastAppliedCtid,
    DateTimeOffset LastAppliedAt, string LastResult);
