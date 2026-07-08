using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Deterministic fake <see cref="IAudioSource"/> for the end-to-end pipeline test: returns scripted
/// SYNTHETIC audio bytes (never personal audio), or simulates an absent staging blob so the
/// orchestrator's <c>failed_terminal</c> path is exercised. Records the fetch so a test can prove the
/// same bytes were threaded to both audio stages.
/// </summary>
internal sealed class FakeAudioSource : IAudioSource
{
    private readonly byte[] _bytes;
    private readonly bool _unavailable;

    public int Fetches { get; private set; }

    public FakeAudioSource(byte[]? bytes = null, bool unavailable = false)
    {
        _bytes = bytes ?? new byte[] { 9, 9, 9, 9 };
        _unavailable = unavailable;
    }

    public Task<ReadOnlyMemory<byte>> FetchAsync(string recordingId, string audioSha256, CancellationToken ct = default)
    {
        Fetches++;
        if (_unavailable)
            throw new AudioUnavailableException($"staging blob for {recordingId} ({audioSha256}) is absent");
        return Task.FromResult<ReadOnlyMemory<byte>>(_bytes);
    }
}

/// <summary>
/// Deterministic fake <see cref="IRecordingFactSource"/>: returns a scripted set of derived facts
/// (summary/entities/dates/links/timeline/attention/participants/garbled-spans). Defaults to a
/// minimal promoted bundle with NO timeline lines (so no map mutation is written under escalate-only);
/// a test can pass timeline lines to exercise the grounded-timeline map-PR path.
/// </summary>
internal sealed class FakeRecordingFactSource(
    string summary = "standup summary",
    IReadOnlyList<ProposedTimelineLine>? timeline = null,
    IReadOnlyList<ResolvedParticipant>? participants = null,
    IReadOnlyList<TextSpan>? garbledSpans = null) : IRecordingFactSource
{
    private readonly IReadOnlyList<ProposedTimelineLine> _timeline = timeline ?? Array.Empty<ProposedTimelineLine>();
    private readonly IReadOnlyList<ResolvedParticipant> _participants = participants ?? Array.Empty<ResolvedParticipant>();
    private readonly IReadOnlyList<TextSpan> _garbled = garbledSpans ?? Array.Empty<TextSpan>();

    public Task<RecordingFacts> GetFactsAsync(
        string recordingId, BaseTranscript baseTranscript, CancellationToken ct = default) =>
        Task.FromResult(new RecordingFacts(
            summary: summary,
            entities: Array.Empty<string>(),
            dates: ["2026-07-04"],
            proposedLinks: Array.Empty<ProposedLink>(),
            proposedTimeline: _timeline,
            attention: BundleAttention.Promote(0.8, "promoted"),
            participants: _participants,
            garbledSpans: _garbled));
}
