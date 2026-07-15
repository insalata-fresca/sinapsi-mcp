using System.Text.Json;
using ApprovalBridge.Broker.Model;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;

namespace ApprovalBridge.Broker.Store;

/// <summary>
/// The durable <see cref="IApprovalStore"/> backed by a JetStream KV bucket (docs/66 §3.1). The
/// one-shot guarantee is delegated to KV's native optimistic concurrency: <see cref="TryConsumeAsync"/>
/// uses <c>UpdateAsync(key, value, expectedRevision)</c>, which the server accepts only when the stored
/// revision still matches — a <see cref="NatsKVWrongLastRevisionException"/> means another approval
/// already won, so this caller loses. State is JSON at rest; the entry holds the server-side nonce.
///
/// <para>Not exercised by CI (no live NATS in the gate) — the one-shot/replay proofs run against
/// <see cref="InMemoryApprovalStore"/>, which models the identical revision-CAS discipline. This class
/// is the production wiring, type-checked against the referenced NATS.Net client.</para>
/// </summary>
internal sealed class JetStreamKvApprovalStore : IApprovalStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly INatsKVStore _kv;

    private JetStreamKvApprovalStore(INatsKVStore kv) => _kv = kv;

    /// <summary>Connect and bind (or create) the KV bucket.</summary>
    public static async Task<JetStreamKvApprovalStore> ConnectAsync(NatsConnection nats, string bucket, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nats);
        var js = new NatsJSContext(nats);
        var kvCtx = new NatsKVContext(js);
        var store = await kvCtx.CreateOrUpdateStoreAsync(new NatsKVConfig(bucket) { History = 8 }, ct);
        return new JetStreamKvApprovalStore(store);
    }

    public async Task<StoredEntry> CreatePendingAsync(PendingEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var pending = entry with { Status = RequestStatus.Pending };
        var rev = await _kv.CreateAsync(entry.RequestId, Serialize(pending), cancellationToken: ct);
        return new StoredEntry(pending, rev);
    }

    public async Task<StoredEntry?> GetAsync(string requestId, CancellationToken ct = default)
    {
        try
        {
            var e = await _kv.GetEntryAsync<string>(requestId, cancellationToken: ct);
            return e.Value is null ? null : new StoredEntry(Deserialize(e.Value), e.Revision);
        }
        catch (NatsKVKeyNotFoundException)
        {
            return null;
        }
    }

    public Task<bool> TryConsumeAsync(string requestId, ulong expectedRevision, string approverIdentity, CancellationToken ct = default)
        => CasAsync(requestId, expectedRevision, RequestStatus.Consumed, approverIdentity, ct);

    public Task<bool> TryTerminateAsync(string requestId, ulong expectedRevision, RequestStatus terminal, string approverIdentity, CancellationToken ct = default)
        => CasAsync(requestId, expectedRevision, terminal, approverIdentity, ct);

    public async Task<IReadOnlyList<StoredEntry>> ListPendingAsync(CancellationToken ct = default)
    {
        var result = new List<StoredEntry>();
        await foreach (var key in _kv.GetKeysAsync(cancellationToken: ct))
        {
            var e = await GetAsync(key, ct);
            if (e is { Value.Status: RequestStatus.Pending }) result.Add(e);
        }
        return result;
    }

    // Optimistic CAS on the KV revision — the whole one-shot guarantee. A revision mismatch (another
    // writer won) or a non-pending current state means this caller loses; both fail closed to false.
    private async Task<bool> CasAsync(string requestId, ulong expectedRevision, RequestStatus next, string approver, CancellationToken ct)
    {
        var current = await GetAsync(requestId, ct);
        if (current is null || current.Revision != expectedRevision || current.Value.Status != RequestStatus.Pending)
            return false;
        var updated = current.Value with { Status = next, ApproverIdentity = approver };
        try
        {
            await _kv.UpdateAsync(requestId, Serialize(updated), expectedRevision, cancellationToken: ct);
            return true;
        }
        catch (NatsKVWrongLastRevisionException)
        {
            return false; // another approval consumed it between our read and our write
        }
    }

    private static string Serialize(PendingEntry e) => JsonSerializer.Serialize(e, Json);
    private static PendingEntry Deserialize(string s) =>
        JsonSerializer.Deserialize<PendingEntry>(s, Json) ?? throw new InvalidDataException("corrupt approval entry");
}
