using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Sinapsi.Nats.EventPlane;

/// <summary>The observed state of an end-to-end change in the idempotency ledger.</summary>
public enum ChangeState
{
    /// <summary>Never seen — safe to begin.</summary>
    Unknown,
    /// <summary>Claimed and executing right now (a concurrent driver holds it).</summary>
    InFlight,
    /// <summary>Executed exactly once already; the recorded result may be replayed to a re-drive.</summary>
    Applied,
}

/// <summary>A ledger record for one end-to-end change-id.</summary>
/// <param name="ChangeId">The stable, end-to-end change-id.</param>
/// <param name="State">Where the change is in its lifecycle.</param>
/// <param name="ResultJson">The recorded NON-SECRET result, present once <see cref="ChangeState.Applied"/>.</param>
public sealed record IdempotencyRecord(string ChangeId, ChangeState State, string? ResultJson);

/// <summary>The outcome of driving an action through <see cref="IdempotentExecutor"/>.</summary>
/// <param name="Executed">True if this call actually ran the effect; false if the recorded result
/// of a prior run was REPLAYED (the safe re-drive after a lost/timed-out ack).</param>
/// <param name="ResultJson">The effect's result — freshly produced or replayed.</param>
public sealed record IdempotentResult(bool Executed, string ResultJson);

/// <summary>
/// Stable, deterministic end-to-end change-id derivation (home-server <c>docs/64 §3</c>: "idempotent
/// actions with an end-to-end change-id"). A retry after a lost ack MUST carry the SAME id, so it can
/// be recognised as the same change and not double-applied. The id is derived only from the action's
/// stable identity — never from a timestamp or a fresh GUID — so an independent re-drive computes the
/// identical id. This is the id an <see cref="ActCommand.CommandId"/> should carry across attempts.
/// </summary>
public static class ChangeId
{
    /// <summary>Derive a stable change-id from an action's identity. Deterministic: identical inputs
    /// always yield the identical id; any field differing yields a different id.</summary>
    /// <param name="kind">The act kind (e.g. <c>merge-pr</c>, <c>deploy</c>).</param>
    /// <param name="target">What is acted on (e.g. <c>ste/sinapsi-mcp#123</c>).</param>
    /// <param name="correlationId">The originating verdict/request trace id.</param>
    public static string Derive(string kind, string target, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("kind is required", nameof(kind));
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("target is required", nameof(target));
        if (string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("correlationId is required", nameof(correlationId));

        // Length-prefixed join so ("ab","c") and ("a","bc") cannot collide.
        var canonical = $"{kind.Length}:{kind}{target.Length}:{target}{correlationId.Length}:{correlationId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "chg_" + Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }
}

/// <summary>The idempotency ledger: an atomic claim + a record of the once-only result. Kept an
/// interface so the CLAIM semantics are testable in-memory without a broker, and a durable
/// (JetStream KV / DB) implementation can be dropped in for the real act-path.</summary>
public interface IIdempotencyStore
{
    /// <summary>Read the current record for <paramref name="changeId"/>, or null if never seen.</summary>
    ValueTask<IdempotencyRecord?> GetAsync(string changeId, CancellationToken ct = default);

    /// <summary>ATOMICALLY claim <paramref name="changeId"/> for execution. Returns true if this
    /// caller won the claim (state → <see cref="ChangeState.InFlight"/>); false if it was already
    /// claimed or applied. This is the single point that makes re-drive safe.</summary>
    ValueTask<bool> TryBeginAsync(string changeId, CancellationToken ct = default);

    /// <summary>Record the once-only result and mark the change <see cref="ChangeState.Applied"/>.</summary>
    ValueTask CompleteAsync(string changeId, string resultJson, CancellationToken ct = default);
}

/// <summary>A process-local, thread-safe <see cref="IIdempotencyStore"/> for tests and single-node
/// use. The atomic claim is a <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>.</summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

    public ValueTask<IdempotencyRecord?> GetAsync(string changeId, CancellationToken ct = default)
        => ValueTask.FromResult(_records.TryGetValue(changeId, out var r) ? r : null);

    public ValueTask<bool> TryBeginAsync(string changeId, CancellationToken ct = default)
    {
        var claimed = _records.TryAdd(changeId, new IdempotencyRecord(changeId, ChangeState.InFlight, null));
        return ValueTask.FromResult(claimed);
    }

    public ValueTask CompleteAsync(string changeId, string resultJson, CancellationToken ct = default)
    {
        _records[changeId] = new IdempotencyRecord(changeId, ChangeState.Applied, resultJson);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Runs an effect AT MOST ONCE per end-to-end change-id (home-server <c>docs/64 §3</c>). The purpose
/// is safe re-drive: when an ack is lost or times out, the caller re-issues the SAME change-id; this
/// executor recognises the change already applied and REPLAYS the recorded result instead of running
/// the effect a second time. A merge/deploy carrying a stable change-id can therefore be retried
/// freely without double-acting.
/// </summary>
public static class IdempotentExecutor
{
    /// <summary>Drive <paramref name="effect"/> under the idempotency ledger.
    /// <list type="bullet">
    ///   <item>Already <see cref="ChangeState.Applied"/> → REPLAY the recorded result, do NOT run
    ///     the effect (<see cref="IdempotentResult.Executed"/> = false). This is the lost-ack re-drive.</item>
    ///   <item>Won the claim → run the effect exactly once, record it, return it (Executed = true).</item>
    ///   <item>Lost the claim to a concurrent in-flight run → throws, rather than risk a double-apply.</item>
    /// </list></summary>
    public static async ValueTask<IdempotentResult> RunOnceAsync(
        IIdempotencyStore store, string changeId,
        Func<CancellationToken, ValueTask<string>> effect, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(effect);
        if (string.IsNullOrWhiteSpace(changeId)) throw new ArgumentException("changeId is required", nameof(changeId));

        var existing = await store.GetAsync(changeId, ct);
        if (existing is { State: ChangeState.Applied })
            return new IdempotentResult(false, existing.ResultJson ?? string.Empty);

        if (!await store.TryBeginAsync(changeId, ct))
        {
            // Someone else claimed it between our read and our claim. Re-read: if they finished, replay.
            var after = await store.GetAsync(changeId, ct);
            if (after is { State: ChangeState.Applied })
                return new IdempotentResult(false, after.ResultJson ?? string.Empty);
            throw new InvalidOperationException(
                $"change '{changeId}' is already in-flight on another driver — refusing to double-apply (docs/64 §3).");
        }

        var result = await effect(ct);
        await store.CompleteAsync(changeId, result, ct);
        return new IdempotentResult(true, result);
    }
}
