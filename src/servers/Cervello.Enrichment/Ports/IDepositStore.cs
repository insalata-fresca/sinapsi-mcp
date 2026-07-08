namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the CAPTURE deposit write (design §5.5 <c>cervello_capture_fact</c>). A captured chat-fact
/// is written as a deposit bundle into <c>conversations/</c> + <c>inbox/</c> in the CT-local cervello
/// working tree, where the E1 ingestion spine picks it up (idempotency key
/// <c>deposit:&lt;depositId&gt;:&lt;commitSha&gt;</c>). The fact enters as a CANDIDATE — it becomes a
/// map fact only through the human GRAPH-ADD gate. This port NEVER writes to <c>map/</c> (design §5.5:
/// "never a silent merge into map/"); it only stages the candidate for review.
///
/// <para>Live = the CT-local working tree (git commit is the deploy's concern); fake = in-memory for
/// tests (no filesystem, no personal data). The write is confirm-gated by the SERVICE, not the store:
/// the store is only reached on <c>confirm=true</c>.</para>
/// </summary>
public interface IDepositStore
{
    /// <summary>
    /// Stage a deposit candidate: a human-readable note in <c>conversations/&lt;id&gt;.md</c> and a
    /// bundle in <c>inbox/&lt;id&gt;/</c>. Returns the commit sha (or a content hash where no git
    /// commit is made) so the caller can form the <c>deposit:&lt;id&gt;:&lt;sha&gt;</c> idempotency key.
    /// Idempotent on <paramref name="depositId"/> (a re-deposit of the same id is a no-op).
    /// </summary>
    Task<DepositResult> WriteAsync(string depositId, string conversationMd, string bundleMd, string dataJson, CancellationToken ct = default);
}

/// <summary>The result of staging a deposit: the primary path written + the commit/content sha.</summary>
public sealed record DepositResult(string Path, string CommitSha);
