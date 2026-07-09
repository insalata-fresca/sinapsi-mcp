using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Deterministic fake Google-<c>.txt</c> base source (the RATIFIED base). Returns a scripted base
/// transcript VERBATIM when one is configured, or <see langword="null"/> (no Google base → the stage
/// degrades gracefully). Records the calls. No staging blob, no network.
/// </summary>
internal sealed class FakeBaseTranscriptSource(BaseTranscript? google) : IBaseTranscriptSource
{
    public int Calls { get; private set; }
    public List<string> Seen { get; } = [];

    /// <summary>A source with NO Google base for any recording (exercises the graceful-degrade path).</summary>
    public static FakeBaseTranscriptSource None() => new((BaseTranscript?)null);

    public Task<BaseTranscript?> GetGoogleBaseAsync(RecordingRef recording, CancellationToken ct = default)
    {
        Calls++;
        Seen.Add(recording.Id);
        return Task.FromResult(google);
    }
}

/// <summary>
/// Deterministic fake CT126 transcription (spec <c>text-correction</c>). Returns a scripted base
/// transcript; records the calls + that the audio is only used transiently. No live endpoint.
/// </summary>
internal sealed class FakeTranscribeClient(BaseTranscript result) : ITranscribeClient
{
    public int Calls { get; private set; }
    public List<(string Format, string Language, int AudioLength)> Seen { get; } = [];

    public Task<BaseTranscript> TranscribeAsync(
        ReadOnlyMemory<byte> audio,
        string format,
        string language,
        CancellationToken ct = default)
    {
        Calls++;
        Seen.Add((format, language, audio.Length));
        return Task.FromResult(result);
    }
}

/// <summary>
/// In-memory <see cref="ITranscriptStore"/> for tests: models <c>recordings/transcripts/&lt;id&gt;.md</c>
/// without touching a working tree, and REFUSES to overwrite an existing base (proving the base
/// is written once and never clobbered).
/// </summary>
internal sealed class InMemoryTranscriptStore : ITranscriptStore
{
    private readonly Dictionary<string, BaseTranscript> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _attributionById = new(StringComparer.Ordinal);

    public string TranscriptPath(string recordingId) => $"recordings/transcripts/{recordingId}.md";

    public Task<bool> ExistsAsync(string recordingId, CancellationToken ct = default) =>
        Task.FromResult(_byId.ContainsKey(recordingId));

    public Task<string> WriteBaseAsync(string recordingId, BaseTranscript transcript, CancellationToken ct = default)
    {
        if (_byId.ContainsKey(recordingId))
            throw new InvalidOperationException(
                $"base transcript for '{recordingId}' already exists — refusing overwrite (base is immutable)");
        _byId[recordingId] = transcript;
        return Task.FromResult(TranscriptPath(recordingId));
    }

    public BaseTranscript? Read(string recordingId) => _byId.GetValueOrDefault(recordingId);

    public string AttributionPath(string recordingId) => $"recordings/attributions/{recordingId}.md";

    public Task<string> WriteAttributionAsync(string recordingId, string markdown, CancellationToken ct = default)
    {
        // NOT write-once — a fresh drain's document supersedes the prior one (mirrors RepoTranscriptStore).
        _attributionById[recordingId] = markdown;
        return Task.FromResult(AttributionPath(recordingId));
    }

    public string? ReadAttribution(string recordingId) => _attributionById.GetValueOrDefault(recordingId);
}
