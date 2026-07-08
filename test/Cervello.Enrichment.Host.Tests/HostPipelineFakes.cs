using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Host.Tests;

// Host-local fakes for the pipeline ports. The engine's TEST-project fakes are internal to
// Cervello.Enrichment.Tests and not visible here, so the host test project supplies its own minimal
// doubles to drive the FULL orchestrator through the drain loop against no DB / no network / no
// audio. They deliberately produce a MINIMAL run (empty diarization → no attribution → no facts
// written) so the drain-loop contract (advance / replay / failure / batch) is what's under test,
// not the enrichment content (that is proven in the engine's EnrichmentPipelineE2ETests).

/// <summary>Returns scripted synthetic audio bytes (never personal audio).</summary>
internal sealed class HostFakeAudioSource : IAudioSource
{
    public Task<ReadOnlyMemory<byte>> FetchAsync(string recordingId, string audioSha256, CancellationToken ct = default) =>
        Task.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1, 2, 3, 4 });
}

/// <summary>Returns a fixed base transcript.</summary>
internal sealed class HostFakeTranscribeClient : ITranscribeClient
{
    public Task<BaseTranscript> TranscribeAsync(
        ReadOnlyMemory<byte> audio, string format, string language, CancellationToken ct = default) =>
        Task.FromResult(new BaseTranscript("transcript", language));
}

/// <summary>In-memory transcript store (never git).</summary>
internal sealed class HostInMemoryTranscriptStore : ITranscriptStore
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    public string TranscriptPath(string recordingId) => $"recordings/transcripts/{recordingId}.md";
    public Task<bool> ExistsAsync(string recordingId, CancellationToken ct = default) => Task.FromResult(_ids.Contains(recordingId));
    public Task<string> WriteBaseAsync(string recordingId, BaseTranscript transcript, CancellationToken ct = default)
    {
        _ids.Add(recordingId);
        return Task.FromResult(TranscriptPath(recordingId));
    }
}

/// <summary>Diarize-embed fake: an EMPTY response (no speakers) by default, or a scripted fault.</summary>
internal sealed class HostFakeDiarizeEmbedClient : IDiarizeEmbedClient
{
    private readonly DiarizeEmbedException? _fault;
    private HostFakeDiarizeEmbedClient(DiarizeEmbedException? fault) => _fault = fault;

    public static HostFakeDiarizeEmbedClient Empty() => new(null);
    public static HostFakeDiarizeEmbedClient Faulting(DiarizeEmbedException fault) => new(fault);

    public Task<DiarizeEmbedResponse> DiarizeEmbedAsync(DiarizeEmbedRequest request, CancellationToken ct = default)
    {
        if (_fault is not null) throw _fault;
        return Task.FromResult(new DiarizeEmbedResponse(
            Array.Empty<DiarizedSegment>(),
            Array.Empty<SpeakerEmbedding>(),
            new DiarizeEmbedModel("silero-vad", "ecapa-tdnn", 192)));
    }
}

/// <summary>Correction LLM fake: proposes nothing.</summary>
internal sealed class HostFakeCorrectionLlm : ICorrectionLlm
{
    public Task<IReadOnlyList<CorrectionCandidate>> ProposeAsync(
        string baseText, CorrectionContext context, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CorrectionCandidate>>(Array.Empty<CorrectionCandidate>());
}

/// <summary>Selective re-ASR fake: never called (no garbled spans).</summary>
internal sealed class HostFakeReAsrClient : IReAsrClient
{
    public Task<ReAsrResult> ReAsrAsync(string recordingId, TextSpan span, CancellationToken ct = default) =>
        Task.FromResult(ReAsrResult.Unclear);
}

/// <summary>Link resolver fake: nothing resolves (no links proposed anyway).</summary>
internal sealed class HostFakeLinkResolver : ILinkResolver
{
    public Task<bool> DossierExistsAsync(string slug, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>Map-PR writer fake: captures the PR (asserts none is opened under the minimal run).</summary>
internal sealed class HostFakeMapPrWriter : IMapPrWriter
{
    public MapReviewPr? LastPr { get; private set; }
    public Task<MapPrHandle> OpenPrAsync(MapReviewPr pr, CancellationToken ct = default)
    {
        LastPr = pr;
        return Task.FromResult(new MapPrHandle(pr.Branch, pr.Title, 100));
    }
}

/// <summary>Pin store fake: deterministic sha.</summary>
internal sealed class HostFakePinStore : IPinStore
{
    public Task<string> PinAsync(string externalRef, CancellationToken ct = default) =>
        Task.FromResult(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(externalRef))).ToLowerInvariant());
}

/// <summary>Derived-fact source fake: a minimal promoted bundle, no timeline lines / participants.</summary>
internal sealed class HostFakeRecordingFactSource : IRecordingFactSource
{
    public Task<RecordingFacts> GetFactsAsync(
        string recordingId, BaseTranscript baseTranscript, CancellationToken ct = default) =>
        Task.FromResult(new RecordingFacts(
            summary: "summary",
            entities: Array.Empty<string>(),
            dates: ["2026-06-01"],
            proposedLinks: Array.Empty<ProposedLink>(),
            proposedTimeline: Array.Empty<ProposedTimelineLine>(),
            attention: BundleAttention.Promote(0.8, "promoted"),
            participants: Array.Empty<ResolvedParticipant>(),
            garbledSpans: Array.Empty<TextSpan>()));
}
