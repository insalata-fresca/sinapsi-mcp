using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="ITranscriptStore"/> that persists the base transcript at
/// <c>recordings/transcripts/&lt;id&gt;.md</c> in the CT-local working tree of <c>ste/cervello</c>
/// (SCHEMAS §8). Write-once: the base is never overwritten by the correction stage (the correction
/// pass emits diffs against it, never a rewrite). Mirrors the <c>InMemoryTranscriptStore</c>
/// contract exactly.
///
/// <para>Confinement: only the derived transcript MARKDOWN is written git-side — never audio, never
/// a biometric vector. The working-tree root is CT-local (<c>/var/lib/cervello/repo</c> by default),
/// the same tree the Watcher writes the manifest into.</para>
/// </summary>
public sealed class RepoTranscriptStore : ITranscriptStore
{
    private readonly string _repoRoot;

    public RepoTranscriptStore(string repoWorkingTree)
    {
        if (string.IsNullOrWhiteSpace(repoWorkingTree))
            throw new ArgumentException("repoWorkingTree must be non-empty", nameof(repoWorkingTree));
        _repoRoot = repoWorkingTree;
    }

    public string TranscriptPath(string recordingId)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        return $"recordings/transcripts/{recordingId}.md";
    }

    public Task<bool> ExistsAsync(string recordingId, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(AbsPath(recordingId)));

    public async Task<string> WriteBaseAsync(string recordingId, BaseTranscript transcript, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var rel = TranscriptPath(recordingId);
        var abs = AbsPath(recordingId);
        // Write-once: refuse to overwrite an existing base transcript (idempotency + no-clobber).
        if (File.Exists(abs))
            return rel;
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        await File.WriteAllTextAsync(abs, transcript.Markdown, ct).ConfigureAwait(false);
        return rel;
    }

    private string AbsPath(string recordingId) => Path.Combine(_repoRoot, TranscriptPath(recordingId));
}
