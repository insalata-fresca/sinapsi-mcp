using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IAudioSource"/> — fetches a recording's TRANSIENT audio bytes from the CT-local
/// staging blob store the <c>Cervello.Watcher</c> writes (its <c>BlobStore</c>: content-addressed by
/// <c>audio_sha256</c> under <c>&lt;stagingRoot&gt;/&lt;sha[..2]&gt;/&lt;sha&gt;.&lt;ext&gt;</c>). The
/// orchestrator calls this ONCE at the top of a recording's run and threads the same bytes to the
/// base-transcribe + diarize stages (neither owns the fetch).
///
/// <para><b>Confinement (invariant 1 / DESIGN §10).</b> The bytes are inference-only: read from the
/// on-CT staging dir, returned to the caller for transient use, and NEVER written git-side (this
/// adapter only READS; the custody guard on the write side lives in the Watcher's <c>BlobStore</c>).
/// The staging root is verified NOT to be under a git working tree at construction, defence-in-depth
/// against a mis-configured <c>CERVELLO_AUDIO_STAGING_DIR</c> pointing at the repo.</para>
///
/// <para><b>Terminal contract (SCHEMAS §5).</b> A missing / unreadable / empty blob throws
/// <see cref="AudioUnavailableException"/> — the orchestrator maps it to <c>failed_terminal</c>: without
/// audio there is nothing to transcribe or diarize and the engine NEVER fabricates a transcript or
/// segments. The adapter probes the recorded <c>audio_sha256</c> across the known audio extensions the
/// Watcher stages (<c>.m4a</c> primary, <c>.wav</c> / no-ext fallbacks) so a re-container by the
/// normalize step does not orphan the blob.</para>
/// </summary>
public sealed class StagingBlobAudioSource : IAudioSource
{
    /// <summary>The audio containers the Watcher may have staged, probed in order (m4a is the Recorder default).</summary>
    private static readonly string[] AudioExtensions = [".m4a", ".wav", ""];

    private readonly string _stagingRoot;

    public StagingBlobAudioSource(string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot))
            throw new ArgumentException("stagingRoot must be non-empty", nameof(stagingRoot));
        var full = Path.GetFullPath(stagingRoot);
        GuardNotUnderGitTree(full);
        _stagingRoot = full;
    }

    public async Task<ReadOnlyMemory<byte>> FetchAsync(
        string recordingId, string audioSha256, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        if (string.IsNullOrWhiteSpace(audioSha256))
            throw new ArgumentException("audioSha256 must be non-empty", nameof(audioSha256));

        // Content-addressed layout, exactly as Cervello.Watcher.Ingest.BlobStore writes it:
        // <stagingRoot>/<sha[..2]>/<sha><ext>. Two-level fan-out by the sha prefix.
        var sha = audioSha256.ToLowerInvariant();
        if (sha.Length < 2)
            throw new AudioUnavailableException(
                $"audio_sha256 for {recordingId} is malformed ('{audioSha256}') — cannot resolve a staging blob");
        var dir = Path.Combine(_stagingRoot, sha[..2]);

        string? found = null;
        foreach (var ext in AudioExtensions)
        {
            var candidate = Path.Combine(dir, sha + ext);
            if (File.Exists(candidate))
            {
                found = candidate;
                break;
            }
        }

        if (found is null)
            throw new AudioUnavailableException(
                $"staging blob for {recordingId} ({sha}) is absent under '{dir}' — the recording cannot be enriched");

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(found, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AudioUnavailableException(
                $"staging blob for {recordingId} ({sha}) is unreadable: {ex.Message}", ex);
        }

        if (bytes.Length == 0)
            throw new AudioUnavailableException(
                $"staging blob for {recordingId} ({sha}) is empty — no transcript/segments may be fabricated");

        return bytes;
    }

    /// <summary>
    /// Reject a staging root that is a git working tree or lives under one (the same custody guard the
    /// Watcher's <c>BlobStore</c> applies on the write side) — audio bytes must never touch git.
    /// </summary>
    private static void GuardNotUnderGitTree(string path)
    {
        var dir = new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        for (var d = dir; d is not null; d = d.Parent)
        {
            var dotGit = Path.Combine(d.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
                throw new InvalidOperationException(
                    $"custody violation: refusing to read audio from under a git working tree " +
                    $"('{d.FullName}' contains .git). The staging dir must be a CT-local path outside any repo.");
        }
    }
}
