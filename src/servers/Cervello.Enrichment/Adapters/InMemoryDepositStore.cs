using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory <see cref="IDepositStore"/> (offline slice / tests). Captures the staged deposit artifacts
/// in a concurrent map so the capture confirm→deposit path is exercised with NO filesystem and NO
/// personal data. Write-once per deposit id (idempotent), mirroring <see cref="RepoDepositStore"/>.
/// </summary>
public sealed class InMemoryDepositStore : IDepositStore
{
    private readonly ConcurrentDictionary<string, DepositRecord> _deposits = new(StringComparer.Ordinal);

    /// <summary>The staged artifacts for a deposit (test-visible).</summary>
    public sealed record DepositRecord(string ConversationMd, string BundleMd, string DataJson);

    /// <summary>Test accessor: the staged artifacts for a deposit id, or null if never written.</summary>
    public DepositRecord? Get(string depositId) => _deposits.TryGetValue(depositId, out var r) ? r : null;

    /// <summary>Whether a deposit was staged (test accessor).</summary>
    public bool Exists(string depositId) => _deposits.ContainsKey(depositId);

    public Task<DepositResult> WriteAsync(string depositId, string conversationMd, string bundleMd, string dataJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(depositId))
            throw new ArgumentException("depositId must be non-empty", nameof(depositId));
        var rel = $"inbox/{depositId}/";
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conversationMd + bundleMd + dataJson))).ToLowerInvariant()[..12];
        _deposits.TryAdd(depositId, new DepositRecord(conversationMd, bundleMd, dataJson)); // write-once
        return Task.FromResult(new DepositResult(rel, sha));
    }
}
