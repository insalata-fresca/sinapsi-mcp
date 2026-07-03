using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Bridge.Mcp.Auth;

/// <summary>
/// In-memory sliding-window rate limiter keyed by (token-hash, bucket).
/// Exact port of the Python RateLimiter in rate_limit.py.
///
/// Buckets:
///   deposit   — deposit_* tools (default 30/min)
///   read      — read/list/search tools (default 60/min)
///   sensitive — lookup_fact(sensitive=True) (default 5/min)
///
/// Key = sha256(token)[:32] + ":" + bucket — token never held in the structure.
/// Window = 60 seconds sliding (timestamps stored as ticks / TimeSpan).
/// Resets on process restart (same as Python single-process design).
/// </summary>
public sealed class BridgeRateLimiter
{
    private const double WindowSeconds = 60.0;

    // Per-(hash+bucket) deque of hit timestamps.
    private readonly ConcurrentDictionary<string, Queue<long>> _buckets = new();
    private readonly object _lock = new();

    public sealed record Decision(bool Allowed, int Remaining, double RetryAfterSeconds);

    /// <summary>
    /// Check whether <paramref name="token"/> is within <paramref name="limitPerMin"/>
    /// for <paramref name="bucket"/>. If allowed, records the hit.
    /// Thread-safe (single coarse lock mirrors Python's threading.Lock).
    /// </summary>
    public Decision Check(string token, string bucket, int limitPerMin)
    {
        var key = MakeKey(token, bucket);
        var now = Environment.TickCount64; // milliseconds
        var cutoffMs = now - (long)(WindowSeconds * 1_000);

        lock (_lock)
        {
            var dq = _buckets.GetOrAdd(key, _ => new Queue<long>());

            // Drop expired entries.
            while (dq.Count > 0 && dq.Peek() < cutoffMs)
                dq.Dequeue();

            if (dq.Count >= limitPerMin)
            {
                // Oldest entry leaves the window at dq.Peek() + windowMs.
                var oldestMs = dq.Peek();
                var retryAfterMs = Math.Max(0.0, oldestMs + WindowSeconds * 1_000 - now);
                return new Decision(false, 0, retryAfterMs / 1_000.0);
            }

            dq.Enqueue(now);
            return new Decision(true, limitPerMin - dq.Count, 0.0);
        }
    }

    // ── Key derivation ────────────────────────────────────────────────────────

    /// <summary>
    /// Key = sha256(utf8(token))[:32] + ":" + bucket.
    /// Matches the Python: hashlib.sha256(token.encode()).hexdigest()[:32] + bucket.
    /// </summary>
    internal static string MakeKey(string token, string bucket)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var hex = Convert.ToHexString(hash).ToLowerInvariant()[..32];
        return hex + ":" + bucket;
    }
}
