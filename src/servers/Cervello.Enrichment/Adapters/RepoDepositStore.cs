using System.Security.Cryptography;
using System.Text;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IDepositStore"/> that stages a capture deposit into the CT-local <c>ste/cervello</c>
/// working tree (design §5.5): a human-readable note at <c>conversations/&lt;id&gt;.md</c> and a
/// bundle at <c>inbox/&lt;id&gt;/{bundle.md, data.json}</c>, where the E1 ingestion spine picks it up.
/// It NEVER writes to <c>map/</c> (design §5.5: the fact is a candidate, not a merge). Write-once per
/// deposit id (idempotent). The git commit + branch push are the deploy's concern — this adapter
/// stages the files and returns a content sha the caller uses for the
/// <c>deposit:&lt;id&gt;:&lt;sha&gt;</c> idempotency key.
///
/// <para>Confinement: only derived markdown/json is staged git-side (never audio, never a vector).</para>
/// </summary>
public sealed class RepoDepositStore : IDepositStore
{
    private readonly string _repoRoot;

    public RepoDepositStore(string repoWorkingTree)
    {
        if (string.IsNullOrWhiteSpace(repoWorkingTree))
            throw new ArgumentException("repoWorkingTree must be non-empty", nameof(repoWorkingTree));
        _repoRoot = repoWorkingTree;
    }

    public async Task<DepositResult> WriteAsync(string depositId, string conversationMd, string bundleMd, string dataJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(depositId))
            throw new ArgumentException("depositId must be non-empty", nameof(depositId));

        var inboxDir = Path.Combine(_repoRoot, "inbox", depositId);
        var convPath = Path.Combine(_repoRoot, "conversations", $"{depositId}.md");
        var rel = $"inbox/{depositId}/";

        // Idempotent: a re-deposit of the same id is a no-op returning the existing content sha.
        if (Directory.Exists(inboxDir))
            return new DepositResult(rel, ContentSha(conversationMd, bundleMd, dataJson));

        Directory.CreateDirectory(inboxDir);
        Directory.CreateDirectory(Path.GetDirectoryName(convPath)!);
        await File.WriteAllTextAsync(Path.Combine(inboxDir, "data.json"), dataJson, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(inboxDir, "bundle.md"), bundleMd, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(convPath, conversationMd, ct).ConfigureAwait(false);
        return new DepositResult(rel, ContentSha(conversationMd, bundleMd, dataJson));
    }

    private static string ContentSha(string a, string b, string c) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(a + b + c))).ToLowerInvariant()[..12];
}
