namespace Sinapsi.SentinelConsole;

/// <summary>
/// The in-memory read-model behind the Console's Operator Approval Bridge lifecycle feed (E1.7,
/// home-server <c>docs/66 §3.5</c>/<c>§6</c> step 3+7). It ingests normalized <see cref="ApprovalEvent"/>s
/// from <c>homelab.security.approval.&gt;</c> and serves the requested→approved/rejected→executed/expired
/// history, joined by <c>correlation_id</c> (mirrors <see cref="ReadModel"/>'s shape exactly — a
/// distinct model because the bridge lifecycle has its own fields, not because the mechanics differ).
///
/// <para>This model does NOT hold the currently-open pending queue with its typed params/title — that
/// view is proxied live from the broker (<see cref="BrokerClient.GetPendingAsync"/>), because the bus
/// envelope deliberately never carries raw params or the registry title (docs/66 §9). This model is the
/// AUDIT TRAIL of what happened; the broker proxy is the CURRENT STATE of what's open.</para>
///
/// Bounded + thread-safe: the feed is a fixed-capacity ring (oldest evicted), so memory is capped
/// regardless of traffic.
/// </summary>
public sealed class ApprovalQueueModel
{
    private readonly int _capacity;
    private readonly LinkedList<ApprovalEvent> _recent = new();   // newest at head
    private readonly object _lock = new();
    private long _total;

    public ApprovalQueueModel(int capacity = 1000) => _capacity = Math.Max(1, capacity);

    public long Total { get { lock (_lock) return _total; } }

    public void Record(ApprovalEvent e)
    {
        lock (_lock)
        {
            _total++;
            _recent.AddFirst(e);
            while (_recent.Count > _capacity) _recent.RemoveLast();
        }
    }

    /// <summary>The most recent <paramref name="n"/> lifecycle events, newest first.</summary>
    public IReadOnlyList<ApprovalEvent> Recent(int n)
    {
        lock (_lock)
            return _recent.Take(Math.Max(0, n)).ToList();
    }

    /// <summary>Every buffered event sharing a correlation id (== request_id), oldest→newest — the
    /// full requested→approved/rejected→executed/expired chain for one request. Empty when the id is
    /// unknown or blank.</summary>
    public IReadOnlyList<ApprovalEvent> Chain(string correlationId)
    {
        if (string.IsNullOrEmpty(correlationId)) return Array.Empty<ApprovalEvent>();
        lock (_lock)
            return _recent
                .Where(e => e.CorrelationId == correlationId)
                .OrderBy(e => e.Time)
                .ToList();
    }
}
