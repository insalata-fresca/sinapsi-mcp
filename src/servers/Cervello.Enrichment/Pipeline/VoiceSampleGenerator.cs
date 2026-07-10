using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// V4 orchestration — turns the voiceprint corpus into <c>unknown_NN</c> Drive samples for the
/// operator to name (design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V4,
/// §4): cluster (V1) → for each voice, pick its representative window (V2 picker over V0 segments) →
/// resolve the recording's audio bytes → cut the clip (V2 cutter) → upload to Drive (V3-pattern) →
/// persist a durable <c>unknown_NN → centroid</c> candidate row (§4.4) so V5's rename-poller can
/// later resolve a renamed Drive file back to the centroid to enroll.
///
/// <para><b>Never fabricates.</b> A voice whose audio cannot be resolved (<see cref="IRecordingAudioRefResolver"/>
/// returns null), whose window cannot be picked (<see cref="RepresentativeSegmentPicker.Pick"/>
/// returns null — no persisted segment ranges, or nothing reaches the minimum window), or whose clip
/// cannot be cut/uploaded is SKIPPED and logged — it never appears as a candidate, never blocks the
/// rest of the run. <see cref="GenerateResult"/> reports both what succeeded and what was skipped
/// (with why) so the caller (the Host endpoint) can surface a truthful summary.</para>
///
/// <para><b>Idempotent-ish re-run (design §5).</b> Each call REPLACES the current UNRESOLVED
/// candidate set (<see cref="IVoiceprintNamingCandidateStore.ReplaceUnresolvedAsync"/>): a prior run's
/// un-renamed candidates are deleted (their stale Drive clips are also best-effort deleted) before the
/// fresh set is inserted. A candidate the operator already renamed→enrolled (V5, marked
/// <see cref="VoiceprintNamingCandidate.Resolved"/>) is NEVER touched — re-running never orphans an
/// already-resolved naming decision.</para>
/// </summary>
public sealed class VoiceSampleGenerator(
    VoiceReviewClusterer clusterer,
    IRecordingVoiceprintStore corpusStore,
    IRecordingAudioRefResolver audioRefResolver,
    IAudioSource audioSource,
    IAudioClipCutter clipCutter,
    IVoiceSampleUploader uploader,
    IVoiceprintNamingCandidateStore candidateStore,
    ILogger<VoiceSampleGenerator>? logger = null)
{
    /// <summary>The uploaded clip's mime type — matches <see cref="FfmpegAudioClipCutter.OutputExtension"/> (m4a).</summary>
    private const string ClipMimeType = "audio/mp4";

    private readonly ILogger _log = logger ?? NullLogger<VoiceSampleGenerator>.Instance;

    public async Task<GenerateResult> GenerateAsync(
        int maxCandidates, CancellationToken ct = default)
    {
        if (maxCandidates <= 0)
            throw new ArgumentException("maxCandidates must be > 0", nameof(maxCandidates));

        var voices = await clusterer.ClusterAsync(maxCandidates: maxCandidates, ct: ct).ConfigureAwait(false);

        var uploaded = new List<GeneratedSample>();
        var skipped = new List<SkippedVoice>();

        for (var i = 0; i < voices.Count; i++)
        {
            var voice = voices[i];
            var sampleName = $"unknown_{i + 1:D2}";
            ct.ThrowIfCancellationRequested();

            try
            {
                var window = await PickWindowAsync(voice, ct).ConfigureAwait(false);
                if (window is null)
                {
                    Skip(skipped, sampleName, voice, "no representative window (no persisted segment ranges, or below the minimum window)");
                    continue;
                }

                var audioRef = await audioRefResolver.ResolveAsync(window.RecordingId, ct).ConfigureAwait(false);
                if (audioRef is null)
                {
                    Skip(skipped, sampleName, voice, $"recording '{window.RecordingId}' audio could not be resolved");
                    continue;
                }

                ReadOnlyMemory<byte> audioBytes;
                try
                {
                    audioBytes = await audioSource.FetchAsync(audioRef.RecordingId, audioRef.AudioSha256, ct).ConfigureAwait(false);
                }
                catch (AudioUnavailableException e)
                {
                    Skip(skipped, sampleName, voice, $"audio unavailable: {e.Message}");
                    continue;
                }

                AudioClip clip;
                try
                {
                    clip = await clipCutter.CutClipAsync(audioBytes, audioRef.Format, window, ct).ConfigureAwait(false);
                }
                catch (AudioClipCutFailedException e)
                {
                    Skip(skipped, sampleName, voice, $"clip cut failed: {e.Message}");
                    continue;
                }

                var fileName = $"{sampleName}.{clip.Format}";
                string driveFileId;
                try
                {
                    driveFileId = await uploader.UploadAsync(fileName, clip.Bytes, ClipMimeType, ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // The uploader's own transport errors are not modelled as a typed exception (it
                    // wraps GatewayMcpClient) — any failure here is terminal for THIS voice only,
                    // never fabricated as a fake file id.
                    Skip(skipped, sampleName, voice, $"Drive upload failed: {e.Message}");
                    continue;
                }

                uploaded.Add(new GeneratedSample(sampleName, driveFileId, voice, window));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Defence in depth: an unexpected exception for one voice must never abort the whole
                // batch (the other ~14 candidates are still worth generating).
                _log.LogWarning(e, "generate-samples: unexpected failure for {SampleName}", sampleName);
                Skip(skipped, sampleName, voice, $"unexpected error: {e.Message}");
            }
        }

        var candidates = uploaded
            .Select(u => new VoiceprintNamingCandidate(
                u.SampleName, u.DriveFileId, u.Voice.RepresentativeCentroid, u.Voice.Members, DateTimeOffset.UtcNow))
            .ToList();

        var deletedFileIds = await candidateStore.ReplaceUnresolvedAsync(candidates, ct).ConfigureAwait(false);

        // Best-effort cleanup: delete the STALE clips the replace just orphaned. A newly-uploaded
        // file id can never appear here (ReplaceUnresolvedAsync only deletes rows that existed
        // BEFORE this call's insert), so this can never delete a sample this very run just uploaded.
        foreach (var staleId in deletedFileIds)
        {
            try
            {
                await uploader.DeleteAsync(staleId, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "generate-samples: failed to delete stale Drive clip {FileId} (best-effort)", staleId);
            }
        }

        _log.LogInformation(
            "generate-samples: {Uploaded} uploaded, {Skipped} skipped, {Deleted} stale clips cleared",
            uploaded.Count, skipped.Count, deletedFileIds.Count);

        return new GenerateResult(candidates, skipped, deletedFileIds);
    }

    /// <summary>
    /// V2's picker needs every contributing segment across the voice's members (V0's
    /// <see cref="IRecordingVoiceprintStore.GetSegmentsAsync"/>), tagged with the owning recording id.
    /// </summary>
    private async Task<RepresentativeWindow?> PickWindowAsync(VoiceReviewCluster voice, CancellationToken ct)
    {
        var tagged = new List<(string RecordingId, DiarizedSegment Segment)>();
        foreach (var member in voice.Members)
        {
            var segments = await corpusStore.GetSegmentsAsync(member.RecordingId, member.ClusterIndex, ct)
                .ConfigureAwait(false);
            foreach (var seg in segments)
                tagged.Add((member.RecordingId, seg));
        }
        return RepresentativeSegmentPicker.Pick(tagged);
    }

    private void Skip(List<SkippedVoice> skipped, string sampleName, VoiceReviewCluster voice, string reason)
    {
        _log.LogInformation("generate-samples: skip {SampleName} ({LocalId}): {Reason}", sampleName, voice.LocalId, reason);
        skipped.Add(new SkippedVoice(sampleName, voice.LocalId, reason));
    }

    private sealed record GeneratedSample(string SampleName, string DriveFileId, VoiceReviewCluster Voice, RepresentativeWindow Window);
}

/// <summary>One voice that could not produce a sample this run, and why (never fabricated — always logged).</summary>
public sealed record SkippedVoice(string SampleName, string VoiceLocalId, string Reason);

/// <summary>The outcome of one <see cref="VoiceSampleGenerator.GenerateAsync"/> call.</summary>
public sealed record GenerateResult(
    IReadOnlyList<VoiceprintNamingCandidate> Uploaded,
    IReadOnlyList<SkippedVoice> Skipped,
    IReadOnlyList<string> DeletedStaleDriveFileIds);
